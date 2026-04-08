using System.Buffers.Binary;
using System.Net.WebSockets;
using Fractatrix.Shared.Packets;
using MessagePack;
using MessagePack.Resolvers;

namespace Fractatrix.Client.Core;

/// <summary>
/// WebSocket connection to a Fractatrix server.
/// Wire format: [PacketId:u16BE][Length:u32BE][MessagePack payload]
/// Thread-safe: sends are serialized via SemaphoreSlim; receive is single-consumer.
/// </summary>
public sealed class FractatrixConnection
{
    internal static readonly MessagePackSerializerOptions MsgOpts =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(StandardResolver.Instance, ContractlessStandardResolver.Instance));

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private const uint MaxPayloadBytes = 256 * 1024;  // 256 KB — largest legit packet is a full flat chunk (~128 KB)

    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private long _rxBytes, _txBytes, _rxPackets, _txPackets;

    public long TotalRxBytes => Interlocked.Read(ref _rxBytes);
    public long TotalTxBytes => Interlocked.Read(ref _txBytes);
    public long TotalRxPackets => Interlocked.Read(ref _rxPackets);
    public long TotalTxPackets => Interlocked.Read(ref _txPackets);

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

    /// <summary>Send a pre-built payload with the given packet ID.</summary>
    public async Task SendRawAsync(ushort packetId, byte[] payload, CancellationToken ct = default)
    {
        if (!IsConnected) return;

        int totalLen = 6 + payload.Length;
        byte[] frame = new byte[totalLen];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), packetId);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2, 4), (uint)payload.Length);
        payload.CopyTo(frame, 6);

        if (!await _sendLock.WaitAsync(SendTimeout, ct)) return;
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open) return;
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(SendTimeout);
            await socket.SendAsync(new ArraySegment<byte>(frame),
                WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);
            Interlocked.Add(ref _txBytes, totalLen);
            Interlocked.Increment(ref _txPackets);
        }
        catch (OperationCanceledException) { }
        finally { _sendLock.Release(); }
    }

    /// <summary>
    /// Receive one packet. Returns (0xFFFF, []) as a sentinel on connection close or error.
    /// Caller loops until the sentinel is received.
    /// </summary>
    public async Task<(ushort Id, byte[] Payload)> ReceiveOneAsync(CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null) return (0xFFFF, []);
        try
        {
            byte[] header = new byte[6];
            if (!await ReadExactAsync(socket, header, 6, ct)) return (0xFFFF, []);

            ushort packetId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));

            if (length > MaxPayloadBytes) return (0xFFFF, []);

            byte[] payload = new byte[length];
            if (length > 0 && !await ReadExactAsync(socket, payload, (int)length, ct))
                return (0xFFFF, []);

            Interlocked.Add(ref _rxBytes, 6 + length);
            Interlocked.Increment(ref _rxPackets);
            return (packetId, payload);
        }
        catch (OperationCanceledException) { return (0xFFFF, []); }
        catch { return (0xFFFF, []); }
    }

    /// <summary>Deserialize a packet payload using the shared MessagePack options.</summary>
    public static T Deserialize<T>(byte[] payload) =>
        MessagePackSerializer.Deserialize<T>(payload, MsgOpts);

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
