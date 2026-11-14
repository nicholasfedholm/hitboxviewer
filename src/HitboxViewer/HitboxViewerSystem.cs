using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace HitboxViewer;

public enum ViewMode { Off, Target, Radius, Keypoints }

/// <summary>
/// Client-only debug viewer for the colliders Combat Overhaul hit-tests.
/// Observation only: nothing is patched, no game state is written.
/// </summary>
public sealed class HitboxViewerSystem : ModSystem
{
    private const string ConfigFile = "HitboxViewer.json";
    private const string HotkeyCode = "hitboxviewer-cycle";

    private ICoreClientAPI? capi;
    private HitboxViewerConfig config = new();
    private HbvRenderer? renderer;

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    /// <summary>One channel, both sides, same message order. Server only answers requests; it never broadcasts.</summary>
    internal const string ChannelName = "hitboxviewer";

    public override void StartServerSide(ICoreServerAPI api)
    {
        IServerNetworkChannel ch = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<HbvZoneRequest>()
            .RegisterMessageType<HbvZoneReply>();
        ch.SetMessageHandler<HbvZoneRequest>((player, req) =>
        {
            try
            {
                HbvZoneReply reply = ServerCoAccess.Answer(api, req);
                if (reply.Entities.Length > 0) ch.SendPacket(reply, player);
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[hitboxviewer] zone request failed: " + ex.Message);
            }
        });
        api.Logger.Notification("[hitboxviewer] 0.2.1 server side loaded; KEYPOINTS zone channel ready, CO " + (ServerCoAccess.CoPresent ? "present" : "absent"));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        config = LoadConfig(api);

        renderer = new HbvRenderer(api, config);
        renderer.SetMode(ParseMode(config.DefaultMode) ?? ViewMode.Off);
        api.Event.RegisterRenderer(renderer, EnumRenderStage.AfterFinalComposition, "hitboxviewer-boxes");
        api.Event.RegisterRenderer(renderer, EnumRenderStage.Ortho, "hitboxviewer-labels");

        // KEYPOINTS: zone data comes from the server. On a server without 0.2.0 the channel never connects and the
        // renderer prints one "no server data" line.
        IClientNetworkChannel ch = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<HbvZoneRequest>()
            .RegisterMessageType<HbvZoneReply>();
        ch.SetMessageHandler<HbvZoneReply>(renderer.OnZoneReply);
        renderer.Channel = ch;

        // Alt+H: free in vanilla (audit-render.md hotkey scan); DLL-only installed mods were not scannable.
        api.Input.RegisterHotKey(HotkeyCode, "Hitbox Viewer: cycle Off/Target/Radius/Keypoints", GlKeys.H, HotkeyType.DevTool, altPressed: true);
        api.Input.SetHotKeyHandler(HotkeyCode, _ =>
        {
            ViewMode next = renderer.Mode switch
            {
                ViewMode.Off => ViewMode.Target,
                ViewMode.Target => ViewMode.Radius,
                ViewMode.Radius => ViewMode.Keypoints,
                _ => ViewMode.Off,
            };
            renderer.SetMode(next);
            api.ShowChatMessage("[hbv] mode " + next);
            return true;
        });

        api.ChatCommands.Create("hbv")
            .WithDescription("Hitbox Viewer: off | target | radius | keypoints | labels | dump")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("action"))
            .HandleWith(OnCommand);

        api.Logger.Notification("[hitboxviewer] 0.2.1 loaded; Combat Overhaul colliders " + (renderer.CoAvailable ? "available" : "NOT available (collision/selection boxes only)"));
    }

    private TextCommandResult OnCommand(TextCommandCallingArgs args)
    {
        if (renderer == null || capi == null) return TextCommandResult.Error("not ready");
        string action = (args[0] as string ?? "").Trim().ToLowerInvariant();
        switch (action)
        {
            case "off":
            case "target":
            case "radius":
            case "keypoints":
                renderer.SetMode(ParseMode(action)!.Value);
                return TextCommandResult.Success("[hbv] mode " + renderer.Mode);
            case "labels":
                renderer.Labels = !renderer.Labels;
                return TextCommandResult.Success("[hbv] labels " + (renderer.Labels ? "on" : "off"));
            case "dump":
                return renderer.Dump(out string msg) ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
            case "":
                return TextCommandResult.Success($"[hbv] mode {renderer.Mode}, labels {(renderer.Labels ? "on" : "off")}, CO {(renderer.CoAvailable ? "available" : "missing")}");
            default:
                return TextCommandResult.Error("usage: .hbv off|target|radius|keypoints|labels|dump");
        }
    }

    private static ViewMode? ParseMode(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "off" => ViewMode.Off,
        "target" => ViewMode.Target,
        "radius" => ViewMode.Radius,
        "keypoints" => ViewMode.Keypoints,
        _ => null,
    };

    private static HitboxViewerConfig LoadConfig(ICoreAPI api)
    {
        HitboxViewerConfig? loaded = null;
        try
        {
            loaded = api.LoadModConfig<HitboxViewerConfig>(ConfigFile);
        }
        catch (Exception e)
        {
            api.Logger.Warning("[hitboxviewer] config unreadable, using defaults: " + e.Message);
        }
        if (loaded == null)
        {
            loaded = new HitboxViewerConfig();
            try { api.StoreModConfig(loaded, ConfigFile); } catch (Exception e) { api.Logger.Warning("[hitboxviewer] could not write config: " + e.Message); }
        }
        if (loaded.EntityCap < 1) loaded.EntityCap = 1;
        if (loaded.EntityCap > 64) loaded.EntityCap = 64;
        if (loaded.MaxDrawDist > 32 || loaded.MaxDrawDist <= 0) loaded.MaxDrawDist = 32;
        if (loaded.Radius > loaded.MaxDrawDist) loaded.Radius = loaded.MaxDrawDist;
        return loaded;
    }

    public override void Dispose()
    {
        if (capi != null && renderer != null)
        {
            capi.Event.UnregisterRenderer(renderer, EnumRenderStage.AfterFinalComposition);
            capi.Event.UnregisterRenderer(renderer, EnumRenderStage.Ortho);
        }
        renderer?.Dispose();
        renderer = null;
        base.Dispose();
    }
}
