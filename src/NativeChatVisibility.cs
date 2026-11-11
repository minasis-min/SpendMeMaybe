using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SpendMeMaybe;

// Adapted from Plugin Chat Overlay (MIT), Copyright 2026 Minasis / min.
internal sealed class NativeChatVisibility(IPluginLog log)
{
    private static readonly string[] Panels = ["ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];
    private static readonly int FlagsOffset = Marshal.OffsetOf<AtkUnitBase>(nameof(AtkUnitBase.Flags198)).ToInt32();
    private static readonly int PanelZoomOffset =
        Marshal.OffsetOf<AddonChatLogPanel>(nameof(AddonChatLogPanel.LogViewer)).ToInt32()
        + Marshal.OffsetOf<LogViewer>(nameof(LogViewer.IsMaximized)).ToInt32();
    private bool? mainCompatible;

    public bool IsExpanded(IGameGui game)
    {
        // Panels have a typed SDK field and do not depend on the main chat fingerprint.
        foreach (var panel in Panels)
            if (ChatVisibilityMemory.ReadExpanded(game.GetAddonByName(panel, 1).Address, PanelZoomOffset, FlagsOffset)) return true;

        try
        {
            if (mainCompatible == null)
            {
                mainCompatible = VerifyMainLayout();
                if (mainCompatible != true)
                    log.Warning("メインチャットの拡大状態を確認できません。独立チャットと標準HUDの判定は継続します。");
            }
            return mainCompatible == true
                && ChatVisibilityMemory.ReadExpanded(game.GetAddonByName("ChatLog", 1).Address, 0x5E8, FlagsOffset);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "メインチャットの拡大判定を停止します。独立チャットと標準HUDの判定は継続します。");
            mainCompatible = false;
            return false;
        }
    }

    private static unsafe bool VerifyMainLayout()
    {
        // Read the resolved function's fingerprint; NEVER invoke IsZoomed.
        Span<byte> code = stackalloc byte[181];
        return AddonVisibilityReader.Read((nint)AddonChatLog.MemberFunctionPointers.IsZoomed, code)
            && ChatVisibilityMemory.MatchesCode(code);
    }
}
