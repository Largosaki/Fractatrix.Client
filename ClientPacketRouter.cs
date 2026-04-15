namespace Fractatrix.Client.Core;

/// <summary>
/// Handler delegate for received packets. The <paramref name="payload"/> memory is
/// only valid for the duration of the call — handlers MUST NOT retain references
/// to it, slice it into long-lived storage, or pass it across an await boundary.
/// To keep data, copy it (e.g. <c>payload.ToArray()</c>) or deserialize into a
/// strongly typed packet immediately.
/// </summary>
public delegate void PacketHandler(ReadOnlyMemory<byte> payload);

/// <summary>
/// Routes incoming packets by ID to registered handler callbacks.
/// Supports multiple handlers per packet ID (multicast).
/// Engine-agnostic equivalent of Godot's old PacketDispatcher.
///
/// <para>Thread-safety: Register/Unregister and Dispatch are safe to call concurrently.
/// Dispatch reads a copy-on-write snapshot under a brief lock, then iterates without
/// allocating (P8 audit fix). Register/Unregister DO take the write lock and rebuild
/// the snapshot — they are not on the hot path. Handlers receive a
/// <see cref="ReadOnlyMemory{Byte}"/> slice over a pooled buffer (Phase 3 #4) —
/// see <see cref="PacketHandler"/> for the lifetime contract.</para>
///
/// <para>Handler equality is delegate structural equality (<c>==</c>), so passing
/// the same method group from the same instance to <see cref="Register"/> and later
/// to <see cref="Unregister"/> works even when each call allocates a fresh delegate
/// (the common case with <c>Register(id, HandleX)</c> shorthand).</para>
/// </summary>
public sealed class ClientPacketRouter
{
    // Per-packet snapshot array. Replaced atomically (copy-on-write) so Dispatch
    // never allocates and never needs a lock.
    private readonly Dictionary<ushort, PacketHandler[]> _handlers = new();
    private readonly HashSet<ushort> _suppressedWarnings = new();
    private readonly object _writeLock = new();
    private readonly Action<string>? _log;

    /// <param name="log">Optional error logger, e.g. <c>Console.Error.WriteLine</c>.</param>
    public ClientPacketRouter(Action<string>? log = null) => _log = log;

    public void Register(ushort packetId, PacketHandler handler)
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
                if (current[i] == handler) return;
            var next = new PacketHandler[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = handler;
            _handlers[packetId] = next;
        }
    }

    public void Unregister(ushort packetId, PacketHandler? handler = null)
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
                if (current[i] == handler) { idx = i; break; }
            if (idx < 0) return;
            if (current.Length == 1)
            {
                _handlers.Remove(packetId);
                return;
            }
            var next = new PacketHandler[current.Length - 1];
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

    public void Dispatch(ushort packetId, ReadOnlyMemory<byte> payload)
    {
        // Read the cached snapshot under the lock only long enough to grab the reference.
        // The array itself is immutable (copy-on-write in Register/Unregister) so iteration
        // is lock-free and allocation-free.
        PacketHandler[] snapshot;
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
