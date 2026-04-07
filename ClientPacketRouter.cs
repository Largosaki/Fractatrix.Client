namespace Fractatrix.Client.Core;

/// <summary>
/// Routes incoming packets by ID to registered handler callbacks.
/// Supports multiple handlers per packet ID (multicast).
/// Engine-agnostic equivalent of Godot's PacketDispatcher.
/// </summary>
public sealed class ClientPacketRouter
{
    private readonly Dictionary<ushort, List<Action<byte[]>>> _handlers = new();
    private readonly HashSet<ushort> _suppressedWarnings = new();
    private readonly Action<string>? _log;

    /// <param name="log">Optional error logger, e.g. <c>Console.Error.WriteLine</c>.</param>
    public ClientPacketRouter(Action<string>? log = null) => _log = log;

    public void Register(ushort packetId, Action<byte[]> handler)
    {
        if (!_handlers.TryGetValue(packetId, out var list))
            _handlers[packetId] = list = new List<Action<byte[]>>(1);
        if (!list.Contains(handler))
            list.Add(handler);
    }

    public void Unregister(ushort packetId, Action<byte[]>? handler = null)
    {
        if (!_handlers.TryGetValue(packetId, out var list)) return;
        if (handler is not null) list.Remove(handler);
        else list.Clear();
        if (list.Count == 0) _handlers.Remove(packetId);
    }

    public int HandlerCount
    {
        get
        {
            int n = 0;
            foreach (var list in _handlers.Values) n += list.Count;
            return n;
        }
    }

    public void Dispatch(ushort packetId, byte[] payload)
    {
        if (!_handlers.TryGetValue(packetId, out var list) || list.Count == 0)
        {
            if (_suppressedWarnings.Add(packetId))
                _log?.Invoke($"[ClientPacketRouter] No handler for 0x{packetId:X4} (len={payload.Length}) — further warnings suppressed.");
            return;
        }
        var snapshot = list.ToArray();
        foreach (var handler in snapshot)
        {
            try { handler(payload); }
            catch (Exception ex)
            {
                _log?.Invoke($"[ClientPacketRouter] Handler for 0x{packetId:X4} threw: {ex.Message}");
            }
        }
    }
}
