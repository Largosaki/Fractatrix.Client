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
    private readonly ClientBlockRegistry _registry;
    private readonly Action<string>?     _log;

    public RegistrySyncHandler(ClientBlockRegistry registry, Action<string>? log = null)
    {
        _registry = registry;
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

        _registry.ApplySnapshot(packet);
        _log?.Invoke($"[RegistrySync] {packet.Blocks.Length} blocks, v={SafeHead(packet.RegistryVersion, 8)}");
    }

    private static string SafeHead(string s, int n) =>
        string.IsNullOrEmpty(s) ? "<none>" : (s.Length >= n ? s[..n] : s);
}
