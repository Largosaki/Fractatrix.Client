using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading.Channels;
using Fractatrix.Shared.Packets;
using MessagePack;
using MessagePack.Resolvers;

namespace Fractatrix.Client.Core;

/// <summary>
/// WebSocket connection to a Fractatrix server.
/// Wire format: [PacketId:u16BE][Length:u32BE][MessagePack payload]
/// Thread-safe: sends are serialized via SemaphoreSlim; receive is single-consumer.
///
/// <para>P8 fixes: outbound queue during disconnect (bounded, drop-oldest), ArrayPool
/// for receive payloads, pooled send frames. Callers of ReceiveOneAsync MUST NOT
/// retain the returned payload slice past the dispatch call — the underlying buffer
/// is rented from <see cref="ArrayPool{T}"/> and released in the recommended usage
/// pattern (see ReceiveOnePooledAsync and the non-pooled back-compat
/// ReceiveOneAsync which copies).</para>
/// </summary>
public sealed class FractatrixConnection
{
    // MessagePack options: StandardResolver only (all packets use [MessagePackObject] + [Key]).
    // UntrustedData security profile caps graph depth to protect against deserialization bombs.
    // Must stay in lockstep with Fractatrix.Shared.Protocol.FractatrixResolver.Options.
    public static readonly MessagePackSerializerOptions MsgOpts =
        MessagePackSerializerOptions.Standard
            .WithResolver(StandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData
                .WithMaximumObjectGraphDepth(Fractatrix.Shared.Constants.MessagePackMaxDepth));

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private const uint MaxPayloadBytes = 256 * 1024;  // 256 KB — largest legit packet is a full flat chunk (~128 KB)

    /// <summary>Max outbound packets buffered while not connected. Drop-oldest on overflow.</summary>
    public const int OutboundQueueCapacity = 256;

    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Outbound queue: packets enqueued while disconnected, flushed on Flush call (caller-driven
    // after successful reconnect + auth).
    private readonly Channel<(ushort Id, byte[] Payload)> _outbound =
        Channel.CreateBounded<(ushort, byte[])>(new BoundedChannelOptions(OutboundQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private long _rxBytes, _txBytes, _rxPackets, _txPackets, _droppedOutbound;

    public long TotalRxBytes => Interlocked.Read(ref _rxBytes);
    public long TotalTxBytes => Interlocked.Read(ref _txBytes);
    public long TotalRxPackets => Interlocked.Read(ref _rxPackets);
    public long TotalTxPackets => Interlocked.Read(ref _txPackets);
    /// <summary>Number of packets currently buffered awaiting send (enqueued while disconnected).</summary>
    public int PendingOutboundCount => _outbound.Reader.Count;
    /// <summary>Total packets dropped from the outbound queue due to overflow (drop-oldest).</summary>
    public long DroppedOutboundPackets => Interlocked.Read(ref _droppedOutbound);

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Connect with a 10-second timeout. Pass your lifecycle CancellationToken.</summary>
    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeout);
        await _socket.ConnectAsync(new Uri(url), connectCts.Token);
    }

    /// <summary>Gracefully close the WebSocket.</summary>
    public async Task DisconnectAsync(string reason = "Disconnected")
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null) return;
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, closeCts.Token);
            }
            catch { /* ignore close errors */ }
        }
        socket.Dispose();
    }

    /// <summary>Serialize an IPacket and send it.</summary>
    public Task SendAsync<T>(T packet, CancellationToken ct = default) where T : IPacket
        => SendRawAsync(packet.PacketId, MessagePackSerializer.Serialize(packet, MsgOpts), ct);

    /// <summary>
    /// Send a pre-built payload with the given packet ID.
    /// If the socket is not currently open, the packet is enqueued in a bounded outbound
    /// buffer (drop-oldest) and will be flushed by <see cref="FlushOutboundAsync"/> once
    /// the caller re-establishes the connection.
    /// </summary>
    public async Task SendRawAsync(ushort packetId, byte[] payload, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            EnqueueOutbound(packetId, payload);
            return;
        }
        await SendFrameAsync(packetId, payload, ct);
    }

    private void EnqueueOutbound(ushort packetId, byte[] payload)
    {
        // Copy the caller's payload defensively so they may mutate/reuse their buffer.
        var copy = new byte[payload.Length];
        Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
        // DropOldest bounded channel: TryWrite always succeeds, but the oldest may be evicted.
        int before = _outbound.Reader.Count;
        _outbound.Writer.TryWrite((packetId, copy));
        // Count drops: if the writer was at capacity, one item was evicted.
        if (before >= OutboundQueueCapacity)
            Interlocked.Increment(ref _droppedOutbound);
    }

    /// <summary>
    /// Flush any packets that were enqueued while disconnected. Call this after the
    /// connection transitions to the Playing state (post-auth). Stops on first send
    /// failure (socket closes again); remaining items stay queued.
    /// </summary>
    public async Task FlushOutboundAsync(CancellationToken ct = default)
    {
        while (IsConnected && _outbound.Reader.TryRead(out var item))
        {
            try
            {
                await SendFrameAsync(item.Id, item.Payload, ct);
            }
            catch
            {
                // Re-queue at the tail on failure and stop. Best-effort ordering.
                _outbound.Writer.TryWrite(item);
                return;
            }
            if (ct.IsCancellationRequested) return;
        }
    }

    private async Task SendFrameAsync(ushort packetId, byte[] payload, CancellationToken ct)
    {
        int totalLen = 6 + payload.Length;
        byte[] frame = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), packetId);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), (uint)payload.Length);
            payload.CopyTo(frame, 6);

            if (!await _sendLock.WaitAsync(SendTimeout, ct)) return;
            try
            {
                var socket = _socket;
                if (socket?.State != WebSocketState.Open)
                {
                    // Lost connection mid-send: re-enqueue.
                    EnqueueOutbound(packetId, payload);
                    return;
                }
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(SendTimeout);
                await socket.SendAsync(new ArraySegment<byte>(frame, 0, totalLen),
                    WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);
                Interlocked.Add(ref _txBytes, totalLen);
                Interlocked.Increment(ref _txPackets);
            }
            catch (OperationCanceledException) { }
            finally { _sendLock.Release(); }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>
    /// Receive one packet. Returns (0xFFFF, []) as a sentinel on connection close or error.
    /// Caller loops until the sentinel is received.
    ///
    /// <para>The returned payload is a freshly-allocated byte[] sized exactly to the payload
    /// length. The header is read via a pooled scratch buffer so the only per-packet allocation
    /// is the payload array itself — no intermediate copy. For zero-alloc steady-state reads
    /// (where the caller can release the buffer after dispatch) see <see cref="ReceiveOnePooledAsync"/>.</para>
    /// </summary>
    public async Task<(ushort Id, byte[] Payload)> ReceiveOneAsync(CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null) return (0xFFFF, []);
        byte[]? header = null;
        try
        {
            header = ArrayPool<byte>.Shared.Rent(6);
            if (!await ReadExactAsync(socket, header, 6, ct)) return (0xFFFF, []);

            ushort packetId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));

            if (length > MaxPayloadBytes) return (0xFFFF, []);

            var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
            if (length > 0 && !await ReadExactAsync(socket, payload, (int)length, ct))
                return (0xFFFF, []);

            Interlocked.Add(ref _rxBytes, 6 + length);
            Interlocked.Increment(ref _rxPackets);
            return (packetId, payload);
        }
        catch (OperationCanceledException) { return (0xFFFF, []); }
        catch { return (0xFFFF, []); }
        finally
        {
            if (header is not null) ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>
    /// Receive one packet using ArrayPool-rented header and payload buffers.
    /// <para><b>Caller MUST Dispose the returned handle</b> (typically in a try/finally
    /// around the handler dispatch) to return the buffer to the pool. The caller
    /// MUST NOT retain any span/slice into <see cref="PooledPayload.Buffer"/> past
    /// the Dispose call.</para>
    /// </summary>
    public async Task<PooledPayload> ReceiveOnePooledAsync(CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null) return PooledPayload.Sentinel;
        byte[]? header = null;
        byte[]? payload = null;
        try
        {
            header = ArrayPool<byte>.Shared.Rent(6);
            if (!await ReadExactAsync(socket, header, 6, ct)) return PooledPayload.Sentinel;

            ushort packetId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));

            if (length > MaxPayloadBytes) return PooledPayload.Sentinel;

            payload = ArrayPool<byte>.Shared.Rent((int)Math.Max(1u, length));
            if (length > 0 && !await ReadExactAsync(socket, payload, (int)length, ct))
                return PooledPayload.Sentinel;

            Interlocked.Add(ref _rxBytes, 6 + length);
            Interlocked.Increment(ref _rxPackets);

            // Release header immediately; payload ownership transfers to PooledPayload.
            ArrayPool<byte>.Shared.Return(header);
            header = null;
            var handle = new PooledPayload(packetId, payload, (int)length);
            payload = null; // ownership transferred
            return handle;
        }
        catch (OperationCanceledException) { return PooledPayload.Sentinel; }
        catch { return PooledPayload.Sentinel; }
        finally
        {
            if (header is not null) ArrayPool<byte>.Shared.Return(header);
            if (payload is not null) ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>Deserialize a packet payload using the shared MessagePack options.</summary>
    public static T Deserialize<T>(byte[] payload) =>
        MessagePackSerializer.Deserialize<T>(payload, MsgOpts);

    /// <summary>Deserialize from a memory slice (zero-copy). Used by handlers that
    /// receive <see cref="ReadOnlyMemory{Byte}"/> from the router hot path.</summary>
    public static T Deserialize<T>(ReadOnlyMemory<byte> payload) =>
        MessagePackSerializer.Deserialize<T>(payload, MsgOpts);

    /// <summary>Deserialize from a pooled payload slice (zero-copy).</summary>
    public static T Deserialize<T>(PooledPayload payload) =>
        MessagePackSerializer.Deserialize<T>(
            new ReadOnlyMemory<byte>(payload.Buffer, 0, payload.Length), MsgOpts);

    private static async Task<bool> ReadExactAsync(
        ClientWebSocket socket, byte[] buf, int count, CancellationToken ct)
    {
        int received = 0;
        while (received < count)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buf, received, count - received), ct);
            if (result.MessageType == WebSocketMessageType.Close) return false;
            received += result.Count;
            if (result.EndOfMessage && received < count) return false;
        }
        return true;
    }
}

/// <summary>
/// Handle to a pooled receive payload. Buffer is rented from <see cref="ArrayPool{T}"/>.
/// <b>Dispose exactly once</b> after the handler finishes. The caller MUST NOT retain
/// any reference to <see cref="Buffer"/> past Dispose.
/// </summary>
public readonly struct PooledPayload : IDisposable
{
    public static readonly PooledPayload Sentinel = new(0xFFFF, Array.Empty<byte>(), 0, pooled: false);

    public ushort Id { get; }
    public byte[] Buffer { get; }
    public int Length { get; }
    private readonly bool _pooled;

    public PooledPayload(ushort id, byte[] buffer, int length)
        : this(id, buffer, length, pooled: true) { }

    private PooledPayload(ushort id, byte[] buffer, int length, bool pooled)
    {
        Id = id;
        Buffer = buffer;
        Length = length;
        _pooled = pooled;
    }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public void Dispose()
    {
        if (_pooled && Buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(Buffer);
    }
}
