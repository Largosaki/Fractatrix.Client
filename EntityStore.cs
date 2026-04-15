using System.Numerics;
using Fractatrix.Shared.Ids;
using Fractatrix.Shared.Packets;

namespace Fractatrix.Client.Core;

/// <summary>
/// Immutable record of one tracked entity. Position uses
/// <see cref="System.Numerics.Vector3"/> so the type is engine-agnostic;
/// Godot consumers convert at the view boundary.
/// </summary>
public sealed record EntityRecord(
    EntityId Id,
    ushort EntityType,
    Vector3 Position,
    byte Yaw,
    byte Pitch);

/// <summary>
/// Engine-agnostic entity tracker. Owns the dictionary of currently-known
/// entities and exposes events for spawn / move / despawn so view layers
/// (Godot EntityRegistry, ATDebug GameClient HTTP queries) can react without
/// each implementing its own packet handlers.
///
/// <para>Threading: the internal dictionary is guarded by a single lock so
/// reads from any thread are safe. Records themselves are immutable so any
/// reference returned from <see cref="GetByGuid"/> / <see cref="All"/> is
/// stable even if the store mutates concurrently.</para>
///
/// <para>Events fire on the same thread that dispatched the underlying packet.
/// In Godot that is the main thread (NetworkManager._Process pumps the
/// receive queue); in ATDebug it is the receive loop thread. Subscribers must
/// be safe for whichever thread their host uses — Godot consumers can touch
/// scene tree, ATDebug consumers cannot block.</para>
/// </summary>
public sealed class EntityStore : IDisposable
{
    private readonly ClientPacketRouter _router;
    private readonly Action<string>? _log;
    private readonly Dictionary<(uint Index, uint Generation), EntityRecord> _byKey = new();
    private readonly Dictionary<Guid, EntityRecord> _byGuid = new();
    /// <summary>Keys whose record was synthesized by an early Move and is still
    /// awaiting its real Spawn. The record exists in <see cref="_byKey"/> with
    /// EntityType=0 and OnSpawn has NOT yet been fired for it.</summary>
    private readonly HashSet<(uint Index, uint Generation)> _synthetic = new();
    private readonly object _lock = new();
    private int _disposed;

    public event Action<EntityRecord>? OnSpawn;
    public event Action<EntityRecord>? OnMove;
    public event Action<EntityRecord, byte>? OnDespawn; // arg2 = DespawnReason

    public EntityStore(ClientPacketRouter router, Action<string>? log = null)
    {
        _router = router;
        _log = log;
        _router.Register(PacketIds.EntitySpawn, HandleSpawn);
        _router.Register(PacketIds.EntityMove, HandleMove);
        _router.Register(PacketIds.EntityDespawn, HandleDespawn);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _router.Unregister(PacketIds.EntitySpawn, HandleSpawn);
        _router.Unregister(PacketIds.EntityMove, HandleMove);
        _router.Unregister(PacketIds.EntityDespawn, HandleDespawn);
    }

    public int Count
    {
        get { lock (_lock) return _byKey.Count; }
    }

    /// <summary>Snapshot of all currently-known entities. The returned array is a copy.</summary>
    public EntityRecord[] All
    {
        get
        {
            lock (_lock)
            {
                var arr = new EntityRecord[_byKey.Count];
                int i = 0;
                foreach (var v in _byKey.Values) arr[i++] = v;
                return arr;
            }
        }
    }

    public EntityRecord? GetByGuid(Guid guid)
    {
        lock (_lock) return _byGuid.TryGetValue(guid, out var r) ? r : null;
    }

    public EntityRecord? GetByKey(uint index, uint generation)
    {
        lock (_lock) return _byKey.TryGetValue((index, generation), out var r) ? r : null;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _byKey.Clear();
            _byGuid.Clear();
            _synthetic.Clear();
        }
    }

    // ── Packet handlers ──────────────────────────────────────────────────

    private void HandleSpawn(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<EntitySpawnPacket>(payload);
            var key = (pkt.Id.Index, pkt.Id.Generation);

            EntityRecord? toEmit = null;
            lock (_lock)
            {
                _byKey.TryGetValue(key, out var existing);
                bool isSynthetic = _synthetic.Remove(key);

                if (existing is null)
                {
                    // Fresh spawn — no prior record.
                    var rec = new EntityRecord(
                        pkt.Id,
                        pkt.EntityType,
                        new Vector3((float)pkt.X, (float)pkt.Y, (float)pkt.Z),
                        Yaw: 0,
                        Pitch: 0);
                    _byKey[key] = rec;
                    if (rec.Id.Guid != Guid.Empty) _byGuid[rec.Id.Guid] = rec;
                    toEmit = rec;
                }
                else if (isSynthetic)
                {
                    // A Move arrived before this Spawn and synthesized a record. Upgrade it
                    // with the real EntityType, but preserve the more recent pose carried
                    // by the Move (server is authoritative on position).
                    var upgraded = existing with
                    {
                        Id = pkt.Id,
                        EntityType = pkt.EntityType,
                    };
                    _byKey[key] = upgraded;
                    if (upgraded.Id.Guid != Guid.Empty) _byGuid[upgraded.Id.Guid] = upgraded;
                    toEmit = upgraded;
                }
                // else: duplicate Spawn for an entity we already know fully — ignore.
            }
            if (toEmit is not null) OnSpawn?.Invoke(toEmit);
        }
        catch (Exception ex) { _log?.Invoke($"[EntityStore] Spawn error: {ex.Message}"); }
    }

    private void HandleMove(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<EntityMovePacket>(payload);
            var key = (pkt.Id.Index, pkt.Id.Generation);
            var pos = new Vector3((float)pkt.X, (float)pkt.Y, (float)pkt.Z);

            EntityRecord? updated = null;
            lock (_lock)
            {
                if (_byKey.TryGetValue(key, out var existing))
                {
                    updated = existing with { Position = pos, Yaw = pkt.Yaw, Pitch = pkt.Pitch };
                    _byKey[key] = updated;
                    if (updated.Id.Guid != Guid.Empty) _byGuid[updated.Id.Guid] = updated;
                }
                else
                {
                    // Move arrived before Spawn — synthesize a record so consumers can still
                    // react to position updates. EntityType is unknown until the real Spawn
                    // arrives; mark the key in _synthetic so HandleSpawn knows to upgrade
                    // (rather than treat the existing key as "already spawned" and drop the
                    // real packet). OnSpawn is NOT fired here — the synthetic record is not
                    // a real entity yet, only a placeholder for the position update.
                    updated = new EntityRecord(pkt.Id, 0, pos, pkt.Yaw, pkt.Pitch);
                    _byKey[key] = updated;
                    if (updated.Id.Guid != Guid.Empty) _byGuid[updated.Id.Guid] = updated;
                    _synthetic.Add(key);
                }
            }
            if (updated is not null) OnMove?.Invoke(updated);
        }
        catch (Exception ex) { _log?.Invoke($"[EntityStore] Move error: {ex.Message}"); }
    }

    private void HandleDespawn(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var pkt = FractatrixConnection.Deserialize<EntityDespawnPacket>(payload);
            var key = (pkt.Id.Index, pkt.Id.Generation);

            EntityRecord? removed = null;
            lock (_lock)
            {
                if (_byKey.TryGetValue(key, out var existing))
                {
                    _byKey.Remove(key);
                    _synthetic.Remove(key);
                    if (existing.Id.Guid != Guid.Empty) _byGuid.Remove(existing.Id.Guid);
                    removed = existing;
                }
            }
            if (removed is not null) OnDespawn?.Invoke(removed, pkt.Reason);
        }
        catch (Exception ex) { _log?.Invoke($"[EntityStore] Despawn error: {ex.Message}"); }
    }
}
