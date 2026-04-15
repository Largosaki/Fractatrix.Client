namespace Fractatrix.Client.Core;

/// <summary>
/// Routes incoming packets by ID to registered handler callbacks.
/// Supports multiple handlers per packet ID (multicast).
/// Engine-agnostic equivalent of Godot's PacketDispatcher.
///
/// <para>Thread-safety: Register/Unregister and Dispatch are safe to call concurrently.
/// Handler snapshots are cached on register/unregister so Dispatch is allocation-free
/// on the hot path (see P8 audit fix).</para>
/// </summary>
public sealed class ClientPacketRouter
{
    private static readonly Action<byte[]>[] Empty = Array.Empty<Action<byte[]>>();

    // Per-packet snapshot array. Replaced atomically (copy-on-write) so Dispatch
    // never allocates and never needs a lock.
    private readonly Dictionary<ushort, Action<byte[]>[]> _handlers = new();
    private readonly HashSet<ushort> _suppressedWarnings = new();
    private readonly object _writeLock = new();
    private readonly Action<string>? _log;

    /// <param name="log">Optional error logger, e.g. <c>Console.Error.WriteLine</c>.</param>
    public ClientPacketRouter(Action<string>? log = null) => _log = log;

    public void Register(ushort packetId, Action<byte[]> handler)
    {
        lock (_writeLock)
        {
            if (!_handlers.TryGetValue(packetId, out var current))
            {
                _handlers[packetId] = new[] { handler };
                return;
            }
            // de-dup
            for (int i = 0; i < current.Length; i++)
                if (ReferenceEquals(current[i], handler)) return;
            var next = new Action<byte[]>[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = handler;
            _handlers[packetId] = next;
        }
    }

    public void Unregister(ushort packetId, Action<byte[]>? handler = null)
    {
        lock (_writeLock)
        {
            if (!_handlers.TryGetValue(packetId, out var current)) return;
            if (handler is null)
            {
                _handlers.Remove(packetId);
                return;
            }
            int idx = -1;
            for (int i = 0; i < current.Length; i++)
                if (ReferenceEquals(current[i], handler)) { idx = i; break; }
            if (idx < 0) return;
            if (current.Length == 1)
            {
                _handlers.Remove(packetId);
                return;
            }
            var next = new Action<byte[]>[current.Length - 1];
            if (idx > 0) Array.Copy(current, 0, next, 0, idx);
            if (idx < current.Length - 1) Array.Copy(current, idx + 1, next, idx, current.Length - idx - 1);
            _handlers[packetId] = next;
        }
    }

    public int HandlerCount
    {
        get
        {
            lock (_writeLock)
            {
                int n = 0;
                foreach (var arr in _handlers.Values) n += arr.Length;
                return n;
            }
        }
    }

    public void Dispatch(ushort packetId, byte[] payload)
    {
        // Read the cached snapshot under the lock only long enough to grab the reference.
        // The array itself is immutable (copy-on-write in Register/Unregister) so iteration
        // is lock-free and allocation-free.
        Action<byte[]>[] snapshot;
        lock (_writeLock)
        {
            if (!_handlers.TryGetValue(packetId, out var arr) || arr.Length == 0)
            {
                if (_suppressedWarnings.Add(packetId))
                    _log?.Invoke($"[ClientPacketRouter] No handler for 0x{packetId:X4} (len={payload.Length}) — further warnings suppressed.");
                return;
            }
            snapshot = arr;
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i](payload); }
            catch (Exception ex)
            {
                _log?.Invoke($"[ClientPacketRouter] Handler for 0x{packetId:X4} threw: {ex.Message}");
            }
        }
    }
}
