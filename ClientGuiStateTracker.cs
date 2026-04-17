namespace Fractatrix.Client.Core;

using System.Collections.Generic;
using Fractatrix.Shared.Packets;

/// <summary>
/// Client-side mirror of the server's open-GUI set. Subscribes to
/// <see cref="OpenGuiPacket"/> / <see cref="CloseGuiPacket"/> on a
/// <see cref="ClientPacketRouter"/> and fires events so UI layers can mount /
/// tear down panels without writing their own dispatchers.
///
/// <para>
/// The tracker records which gui ids are currently open and the initial state
/// payload that arrived with each <see cref="OpenGuiPacket"/>. Consumers (Godot
/// panel manager, ATDebug HTTP probe) can query <see cref="IsOpen"/> /
/// <see cref="GetState"/> or subscribe to the events.
/// </para>
/// </summary>
public sealed class ClientGuiStateTracker
{
    private readonly Dictionary<string, byte[]?> _open = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;

    /// <summary>Fires when the server opened a GUI. Args: (guiId, initial state payload or null).</summary>
    public event Action<string, byte[]?>? OnOpen;

    /// <summary>Fires when a GUI closes (server-forced or client-initiated echo).</summary>
    public event Action<string>? OnClose;

    public ClientGuiStateTracker(Action<string>? log = null)
    {
        _log = log;
    }

    public IReadOnlyCollection<string> Open => [.. _open.Keys];
    public bool IsOpen(string guiId)   => _open.ContainsKey(guiId);
    public byte[]? GetState(string id) => _open.TryGetValue(id, out var s) ? s : null;

    /// <summary>Subscribe this tracker to the router's packet dispatch.</summary>
    public void Register(ClientPacketRouter router)
    {
        router.Register(PacketIds.OpenGui,  OnOpenPacket);
        router.Register(PacketIds.CloseGui, OnClosePacket);
    }

    public void Unregister(ClientPacketRouter router)
    {
        router.Unregister(PacketIds.OpenGui,  OnOpenPacket);
        router.Unregister(PacketIds.CloseGui, OnClosePacket);
    }

    private void OnOpenPacket(ReadOnlyMemory<byte> payload)
    {
        OpenGuiPacket pkt;
        try { pkt = FractatrixConnection.Deserialize<OpenGuiPacket>(payload); }
        catch (Exception ex)
        {
            _log?.Invoke($"[Gui] OpenGui decode failed: {ex.Message}");
            return;
        }
        _open[pkt.GuiId] = pkt.State;
        _log?.Invoke($"[Gui] open {pkt.GuiId} (state={(pkt.State?.Length ?? 0)} bytes)");
        OnOpen?.Invoke(pkt.GuiId, pkt.State);
    }

    private void OnClosePacket(ReadOnlyMemory<byte> payload)
    {
        CloseGuiPacket pkt;
        try { pkt = FractatrixConnection.Deserialize<CloseGuiPacket>(payload); }
        catch (Exception ex)
        {
            _log?.Invoke($"[Gui] CloseGui decode failed: {ex.Message}");
            return;
        }
        if (_open.Remove(pkt.GuiId))
        {
            _log?.Invoke($"[Gui] close {pkt.GuiId}");
            OnClose?.Invoke(pkt.GuiId);
        }
    }
}
