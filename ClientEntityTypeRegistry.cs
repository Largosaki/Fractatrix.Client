namespace Fractatrix.Client.Core;

using System.Collections.Generic;
using Fractatrix.Shared.Packets;

/// <summary>
/// Client-side entity type registry. Populated by <see cref="RegistrySyncPacket"/>
/// at session join. Provides bidirectional lookup between wire int ids (appearing
/// on <c>EntitySpawnPacket.EntityType</c>) and stable namespaced names.
///
/// <para>
/// Unknown int ids / names return <see cref="UnknownEntry"/> — a <c>fractatrix:missing_entity</c>
/// placeholder. View layers should render it as a visible glitch (magenta capsule
/// with a label) so bugs are obvious rather than silent.
/// </para>
/// </summary>
public sealed class ClientEntityTypeRegistry
{
    public const string UnknownName = "fractatrix:missing_entity";

    public static readonly EntityRegistryEntry UnknownEntry = new()
    {
        Id          = -1,
        Name        = UnknownName,
        DisplayName = null,
        ModelKey    = null,
        Tags        = null,
    };

    private readonly Dictionary<int, EntityRegistryEntry>    _byId   = new();
    private readonly Dictionary<string, EntityRegistryEntry> _byName = new(StringComparer.Ordinal);
    private string _version = string.Empty;

    public string Version => _version;
    public int    Count   => _byId.Count;

    /// <summary>Fires synchronously after each <see cref="ApplySnapshot"/>.</summary>
    public event Action? OnSnapshotApplied;

    public void ApplySnapshot(EntityRegistryEntry[] entries, string version)
    {
        _byId.Clear();
        _byName.Clear();
        foreach (var e in entries)
        {
            _byId[e.Id]     = e;
            _byName[e.Name] = e;
        }
        _version = version;
        OnSnapshotApplied?.Invoke();
    }

    public bool IsKnown(int id)      => _byId.ContainsKey(id);
    public bool IsKnown(string name) => _byName.ContainsKey(name);

    public EntityRegistryEntry GetEntry(int id) =>
        _byId.TryGetValue(id, out var e) ? e : UnknownEntry;

    public EntityRegistryEntry GetEntry(string name) =>
        _byName.TryGetValue(name, out var e) ? e : UnknownEntry;

    public string GetName(int id) => GetEntry(id).Name;

    public int GetId(string name) =>
        _byName.TryGetValue(name, out var e) ? e.Id : -1;

    public IReadOnlyCollection<EntityRegistryEntry> Entries => _byId.Values;
}
