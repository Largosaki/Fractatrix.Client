namespace Fractatrix.Client.Core;

using Fractatrix.Shared.Packets;

/// <summary>
/// Engine-agnostic glue that connects <see cref="RegistrySyncPacket"/> dispatch on a
/// <see cref="ClientPacketRouter"/> to a <see cref="ClientBlockRegistry"/>.
///
/// <para>
/// Use from any client entry point (Godot autoload, ATDebug console app) by
/// constructing with a registry + logger and calling <see cref="Register"/> once
/// with the router. Unregister when the session tears down.
/// </para>
/// </summary>
public sealed class RegistrySyncHandler
{
    private readonly ClientBlockRegistry      _blocks;
    private readonly ClientEntityTypeRegistry _entities;
    private readonly ClientGuiSchemaRegistry  _guis;
    private readonly Action<string>?          _log;

    public RegistrySyncHandler(
        ClientBlockRegistry      blocks,
        ClientEntityTypeRegistry entities,
        ClientGuiSchemaRegistry  guis,
        Action<string>? log = null)
    {
        _blocks   = blocks;
        _entities = entities;
        _guis     = guis;
        _log      = log;
    }

    public void Register(ClientPacketRouter router) =>
        router.Register(PacketIds.RegistrySync, OnPacket);

    public void Unregister(ClientPacketRouter router) =>
        router.Unregister(PacketIds.RegistrySync, OnPacket);

    private void OnPacket(ReadOnlyMemory<byte> payload)
    {
        RegistrySyncPacket packet;
        try
        {
            packet = FractatrixConnection.Deserialize<RegistrySyncPacket>(payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[RegistrySync] Decode failed: {ex.Message}");
            return;
        }

        _blocks.ApplySnapshot(packet);
        _entities.ApplySnapshot(packet.Entities, packet.RegistryVersion);
        _guis.ApplySnapshot(packet.GuiSchemas, packet.RegistryVersion);

        _log?.Invoke(
            $"[RegistrySync] blocks={packet.Blocks.Length} entities={packet.Entities.Length} " +
            $"guis={packet.GuiSchemas.Length} v={SafeHead(packet.RegistryVersion, 8)}");
    }

    private static string SafeHead(string s, int n) =>
        string.IsNullOrEmpty(s) ? "<none>" : (s.Length >= n ? s[..n] : s);
}
