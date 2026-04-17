namespace Fractatrix.Client.Core;

using System.Collections.Generic;
using Fractatrix.Shared.Packets;

/// <summary>
/// Client-side block registry. Populated by <see cref="RegistrySyncPacket"/> at session
/// join. Provides bidirectional lookup between session-local int IDs and stable
/// namespaced string names, plus access to per-block structural metadata (solidity,
/// opacity, light emission, texture key).
///
/// <para>
/// <b>Unknown-ID behavior</b>: lookups for IDs or names not present in the current
/// session's snapshot return <see cref="UnknownEntry"/> — a hard-coded
/// <c>fractatrix:missing</c> placeholder rendered as an opaque solid magenta cube.
/// Callers should prefer <see cref="IsKnown(int)"/> when they need to distinguish
/// "actually missing" from "resolved to placeholder".
/// </para>
///
/// <para>
/// Thread-safety: <see cref="ApplySnapshot(RegistrySyncPacket)"/> is not thread-safe
/// and should be called from the single dispatch thread. Lookups are read-only and
/// safe once the snapshot is stable.
/// </para>
/// </summary>
public sealed class ClientBlockRegistry
{
    /// <summary>Canonical name for blocks not present in the session's registry.</summary>
    public const string UnknownName = "fractatrix:missing";

    /// <summary>
    /// Hard-coded fallback returned for unknown IDs / names. Opaque + solid so the
    /// placeholder has a correct cube mesh (the rendering layer substitutes the
    /// magenta missing-texture on top).
    /// </summary>
    public static readonly BlockRegistryEntry UnknownEntry = new()
    {
        Id            = -1,
        Name          = UnknownName,
        DisplayName   = null,
        TextureKey    = null,
        IsSolid       = true,
        IsOpaque      = true,
        LightEmission = 0,
        Tags          = null,
    };

    private readonly Dictionary<int, BlockRegistryEntry>    _byId   = new();
    private readonly Dictionary<string, BlockRegistryEntry> _byName = new(StringComparer.Ordinal);
    private string _version = string.Empty;

    /// <summary>Content hash sent by the server with the last <see cref="ApplySnapshot"/>.</summary>
    public string Version => _version;

    public int Count => _byId.Count;

    /// <summary>
    /// Fires synchronously after each successful <see cref="ApplySnapshot"/>.
    /// Subscribers (e.g. BlockColorTable, ChunkMeshBuilder caches) can rebuild
    /// their id-indexed views here. Called on the dispatch thread.
    /// </summary>
    public event Action? OnSnapshotApplied;

    /// <summary>Replace the registry contents with a new snapshot from the server.</summary>
    public void ApplySnapshot(RegistrySyncPacket packet)
    {
        _byId.Clear();
        _byName.Clear();
        foreach (var e in packet.Blocks)
        {
            _byId[e.Id]     = e;
            _byName[e.Name] = e;
        }
        _version = packet.RegistryVersion;
        OnSnapshotApplied?.Invoke();
    }

    public bool IsKnown(int id)        => _byId.ContainsKey(id);
    public bool IsKnown(string name)   => _byName.ContainsKey(name);

    /// <summary>Resolve an entry by wire id. Returns <see cref="UnknownEntry"/> if not found.</summary>
    public BlockRegistryEntry GetEntry(int id) =>
        _byId.TryGetValue(id, out var e) ? e : UnknownEntry;

    /// <summary>Resolve an entry by name. Returns <see cref="UnknownEntry"/> if not found.</summary>
    public BlockRegistryEntry GetEntry(string name) =>
        _byName.TryGetValue(name, out var e) ? e : UnknownEntry;

    public string GetName(int id) => GetEntry(id).Name;

    /// <summary>Reverse lookup: name → id, or -1 if the name is not registered.</summary>
    public int GetId(string name) =>
        _byName.TryGetValue(name, out var e) ? e.Id : -1;

    /// <summary>All entries, in no particular order. Do not mutate.</summary>
    public IReadOnlyCollection<BlockRegistryEntry> Entries => _byId.Values;
}
