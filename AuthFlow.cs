using Fractatrix.Shared;
using Fractatrix.Shared.Packets;

namespace Fractatrix.Client.Core;

/// <summary>
/// Result of an <see cref="AuthFlow.RunAsync"/> call.
/// </summary>
public sealed record AuthResult(bool Success, Guid PlayerId, string Message);

/// <summary>
/// Engine-agnostic auth state machine: drives Handshake → AuthRequest → AuthResponse,
/// auto-acks server KeepAlive (with RTT measurement), and surfaces server-initiated
/// Disconnect packets via <see cref="OnServerDisconnect"/>.
///
/// <para>One AuthFlow per connection attempt. Construct it with the
/// <see cref="FractatrixConnection"/> and the <see cref="ClientPacketRouter"/> that
/// dispatches received packets; AuthFlow auto-registers its packet handlers in the
/// constructor and unregisters them in <see cref="Dispose"/>.</para>
///
/// <para>Threading: AuthFlow makes no main-thread assumptions. It uses
/// <see cref="TaskCompletionSource{T}"/> with <c>RunContinuationsAsynchronously</c>
/// so it works whether handler callbacks run on the receive thread (ATDebug)
/// or are marshalled to the main thread (Godot via NetworkManager._Process).</para>
/// </summary>
public sealed class AuthFlow : IDisposable
{
    private const long KeepAliveIntervalMs = 5000;

    private readonly FractatrixConnection _conn;
    private readonly ClientPacketRouter _router;
    private readonly Action<string>? _log;
    private readonly TimeSpan _timeout;

    private TaskCompletionSource<HandshakeResponsePacket>? _hsTcs;
    private TaskCompletionSource<AuthResponsePacket>? _authTcs;
    private long _lastKeepAliveMs = -1;
    private int _disposed;

    /// <summary>RTT estimate from KeepAlive timing (interval delta minus expected 5s). Updated on each KeepAlive.</summary>
    public float LastRttMs { get; private set; }

    /// <summary>PlayerId from the most recent successful auth, or <see cref="Guid.Empty"/>.</summary>
    public Guid PlayerId { get; private set; }

    /// <summary>Fired when the server sends a <see cref="DisconnectPacket"/>. The argument is the server-supplied message.</summary>
    public event Action<string>? OnServerDisconnect;

    public AuthFlow(
        FractatrixConnection conn,
        ClientPacketRouter router,
        Action<string>? log = null,
        TimeSpan? timeout = null)
    {
        _conn = conn;
        _router = router;
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);

        _router.Register(PacketIds.HandshakeResponse, OnHandshakeResponse);
        _router.Register(PacketIds.AuthResponse, OnAuthResponse);
        _router.Register(PacketIds.KeepAlive, OnKeepAlive);
        _router.Register(PacketIds.Disconnect, OnDisconnectPacket);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _router.Unregister(PacketIds.HandshakeResponse, OnHandshakeResponse);
        _router.Unregister(PacketIds.AuthResponse, OnAuthResponse);
        _router.Unregister(PacketIds.KeepAlive, OnKeepAlive);
        _router.Unregister(PacketIds.Disconnect, OnDisconnectPacket);
        _hsTcs?.TrySetCanceled();
        _authTcs?.TrySetCanceled();
    }

    /// <summary>
    /// Drive the full Handshake → Auth sequence. The connection must already be
    /// open and the receive loop must already be dispatching packets to the router
    /// passed to the constructor (otherwise this call will block until timeout).
    /// </summary>
    public async Task<AuthResult> RunAsync(string playerName, string token, CancellationToken ct = default)
    {
        _hsTcs = new TaskCompletionSource<HandshakeResponsePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _authTcs = new TaskCompletionSource<AuthResponsePacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var reg = cts.Token.Register(() =>
        {
            _hsTcs?.TrySetCanceled();
            _authTcs?.TrySetCanceled();
        });

        try
        {
            await _conn.SendAsync(new HandshakeRequestPacket
            {
                ProtocolVersion = Constants.ProtocolVersion,
                PlayerName = playerName,
            }, ct);
        }
        catch (Exception ex)
        {
            return new AuthResult(false, Guid.Empty, $"Send Handshake failed: {ex.Message}");
        }

        HandshakeResponsePacket hsResp;
        try { hsResp = await _hsTcs.Task; }
        catch (OperationCanceledException) { return new AuthResult(false, Guid.Empty, "Handshake timeout"); }

        if (!hsResp.Accepted)
            return new AuthResult(false, Guid.Empty, hsResp.RejectReason ?? "Handshake rejected");

        try
        {
            await _conn.SendAsync(new AuthRequestPacket { Token = token }, ct);
        }
        catch (Exception ex)
        {
            return new AuthResult(false, Guid.Empty, $"Send Auth failed: {ex.Message}");
        }

        AuthResponsePacket authResp;
        try { authResp = await _authTcs.Task; }
        catch (OperationCanceledException) { return new AuthResult(false, Guid.Empty, "Auth timeout"); }

        if (!authResp.Success)
            return new AuthResult(false, Guid.Empty, authResp.Message ?? "Auth failed");

        PlayerId = authResp.PlayerId;
        return new AuthResult(true, authResp.PlayerId, authResp.Message ?? string.Empty);
    }

    // ── Packet handlers ──────────────────────────────────────────────────

    private void OnHandshakeResponse(byte[] payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<HandshakeResponsePacket>(payload);
            _hsTcs?.TrySetResult(pkt);
        }
        catch (Exception ex) { _log?.Invoke($"[AuthFlow] HandshakeResponse parse error: {ex.Message}"); }
    }

    private void OnAuthResponse(byte[] payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<AuthResponsePacket>(payload);
            _authTcs?.TrySetResult(pkt);
        }
        catch (Exception ex) { _log?.Invoke($"[AuthFlow] AuthResponse parse error: {ex.Message}"); }
    }

    private void OnKeepAlive(byte[] payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<KeepAlivePacket>(payload);
            _ = _conn.SendAsync(new KeepAliveAckPacket { Timestamp = pkt.Timestamp });

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastKeepAliveMs > 0)
            {
                long elapsed = now - _lastKeepAliveMs;
                LastRttMs = Math.Max(0f, elapsed - KeepAliveIntervalMs);
            }
            _lastKeepAliveMs = now;
        }
        catch (Exception ex) { _log?.Invoke($"[AuthFlow] KeepAlive parse error: {ex.Message}"); }
    }

    private void OnDisconnectPacket(byte[] payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<DisconnectPacket>(payload);
            OnServerDisconnect?.Invoke(pkt.Message ?? string.Empty);
        }
        catch (Exception ex) { _log?.Invoke($"[AuthFlow] Disconnect parse error: {ex.Message}"); }
    }
}
