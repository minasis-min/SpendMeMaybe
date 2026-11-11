using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SpendMeMaybe;

// Follow the native HP/MP HUD (DelvUI approach), with explicit chat expansion detection.
// Independent implementation; DelvUI is not a dependency.
internal sealed class NativeUiVisibility(IPluginLog log)
{
    private int? flagsOffset;
    private bool warned;
    private readonly NativeChatVisibility chat = new(log);
    public bool HudVisible(IGameGui game, bool cutsceneActive)
    {
        try
        {
            flagsOffset ??= Marshal.OffsetOf<AtkUnitBase>(nameof(AtkUnitBase.Flags198)).ToInt32();
            // FadeMiddle can outlive the cutscene. Do not wait for its Show bit to clear.
            return NativeHudVisibility.ShouldShow(Read(game, "_ParameterWidget"), chat.IsExpanded(game), cutsceneActive);
        }
        catch (Exception ex)
        {
            if (!warned) { log.Warning(ex, "標準HUDの表示状態を取得できないため、通常表示を維持します。"); warned = true; }
            return !cutsceneActive;
        }
    }
    private bool? Read(IGameGui game, string name)
    {
        var addon = game.GetAddonByName(name, 1).Address;
        // IsVisible is the Show bit of VisibilityState (Flags198 bits 20..23).
        var showMask = (uint)AtkUnitBaseVisibilityState.Show << 20;
        return AddonVisibilityReader.ReadVisible(addon, flagsOffset!.Value, showMask);
    }
}
