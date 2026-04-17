namespace Fractatrix.Client.Core;

using System.Collections.Generic;
using Fractatrix.Shared.Packets;

/// <summary>
/// Client-side GUI schema registry. Populated by <see cref="RegistrySyncPacket"/>
/// at session join. Consumed when an <c>OpenGuiPacket</c> arrives — the client
/// looks up the schema by id and renders the declared title / background.
///
/// <para>
/// Unknown GUI ids render with <see cref="UnknownSchema"/>'s placeholder — an empty
/// window labelled with the unknown id. Prevents "open GUI" crashes on a version
/// mismatch; visible enough that the bug surfaces during testing.
/// </para>
/// </summary>
public sealed class ClientGuiSchemaRegistry
{
    public const string UnknownId = "fractatrix:gui/missing";

    public static readonly GuiSchemaEntry UnknownSchema = new()
    {
        Id            = UnknownId,
        Title         = null,
        BackgroundKey = null,
    };

    private readonly Dictionary<string, GuiSchemaEntry> _byId = new(StringComparer.Ordinal);
    private string _version = string.Empty;

    public string Version => _version;
    public int    Count   => _byId.Count;

    public event Action? OnSnapshotApplied;

    public void ApplySnapshot(GuiSchemaEntry[] entries, string version)
    {
        _byId.Clear();
        foreach (var e in entries)
            _byId[e.Id] = e;
        _version = version;
        OnSnapshotApplied?.Invoke();
    }

    public bool IsKnown(string id) => _byId.ContainsKey(id);

    public GuiSchemaEntry Get(string id) =>
        _byId.TryGetValue(id, out var s) ? s : UnknownSchema;

    public IReadOnlyCollection<GuiSchemaEntry> Entries => _byId.Values;
}
