// Adapted from Minasis Tweaks (OrbitMovement), MIT, Copyright 2026 Minasis.
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;

namespace SpendMeMaybe;

internal sealed unsafe class TeleportCommandCopyFeature : IDisposable
{
    private readonly IContextMenu menus;
    private readonly IDataManager data;
    private readonly IClientState client;
    private readonly IPluginLog log;
    private readonly Func<bool> enabled;
    private readonly Func<TeleportCommandProvider> provider;
    private readonly object gate;
    private bool disposed;
    private ushort? prefixColor;
    public string Status { get; private set; } = "待機中";
    public TeleportCopyCompatibility Report { get; }
    public TeleportCommandCopyFeature(IContextMenu menus, IDataManager data, IClientState client, IPluginLog log,
        object gate, Func<bool> enabled, Func<TeleportCommandProvider> provider)
    {
        this.gate=gate; this.menus=menus; this.data=data; this.client=client; this.log=log; this.enabled=enabled; this.provider=provider;
        Report=TeleportCopyCompatibility.Check();
        if (!Report.Compatible) { Status="この環境では利用できません。ログを確認してください"; log.Warning("テレポコマンドコピーを停止: {Reason}", Report.Detail); }
        menus.OnMenuOpened+=OnMenu;
    }
    private TeleportCommandDestination Describe(TeleportInfo info)
    {
        var sheet=data.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
        if (!sheet.TryGetRow(info.AetheryteId,out var row)) return new(info.AetheryteId,info.SubIndex,info.Ward,info.Plot,"","");
        return new(info.AetheryteId,info.SubIndex,info.Ward,info.Plot,
            row.PlaceName.ValueNullable?.Name.ExtractText() ?? "", row.AethernetName.ValueNullable?.Name.ExtractText() ?? "");
    }
    private void OnMenu(IMenuOpenedArgs args) { lock (gate) OnMenuLocked(args); }
    private ushort GetPrefixColor()
    {
        if (prefixColor is { } cached) return cached;
        var colors = data.GetExcelSheet<Lumina.Excel.Sheets.UIColor>().Select(row => (row.RowId, row.Dark));
        if (!TeleportMenuColor.TrySelect(colors, out var selected)) throw new InvalidDataException("メニューアイコンの色を取得できません。");
        prefixColor = selected;
        return selected;
    }
    private void OnMenuLocked(IMenuOpenedArgs args)
    {
        if(disposed || !enabled() || !client.IsLoggedIn || args.MenuType!=ContextMenuType.Default || args.AddonName!="Teleport") return;
        if(!Report.Compatible) { Status="この環境では利用できません。ログを確認してください"; return; }
        try {
            var owner=(AtkUnitBase*)args.AddonPtr;
            var agent=AgentTeleport.Instance();
            if(owner==null || !owner->IsVisible || agent==null || agent->AddonId!=owner->Id || agent->AetheryteList==null) return;
            var count=agent->AetheryteList->LongCount;
            var index=agent->ContextMenuAetheryteIndex;
            if(count is <1 or >1024 || agent->AetheryteCount is <1 or >1024 || index>=count || index>=agent->AetheryteCount) { Status="行き先一覧を開き直してください"; return; }
            var selected=Describe((*agent->AetheryteList)[index]);
            var candidates=new List<TeleportCommandDestination>((int)count);
            for(var i=0;i<count;i++) candidates.Add(Describe((*agent->AetheryteList)[i]));
            var format=provider();
            var valid=TeleportCommandCopy.TryBuild(format,selected,candidates,out var command,out var reason);
            if(!valid) Status=reason;
            args.AddMenuItem(new MenuItem {
                Name="コマンドをコピー", PrefixChar='S', PrefixColor=GetPrefixColor(), IsEnabled=valid,
                OnClicked=_ => { lock (gate) {
                    if(disposed || !enabled() || !valid || !client.IsLoggedIn) return;
                    if(provider()!=format) { Status="コピー形式が変更されました。右クリックし直してください"; return; }
                    try { ImGui.SetClipboardText(command); Status="コピーしました: "+command; }
                    catch(Exception e) { Status="コピーできませんでした"; log.Error(e,"Teleport command clipboard failed"); }
                }}
            });
        } catch(Exception e) { Status="行き先を取得できませんでした"; log.Error(e,"Teleport command menu failed"); }
    }
    public void Dispose() { lock (gate) { if(disposed)return; disposed=true; menus.OnMenuOpened-=OnMenu; } }
}

// Preserve the source feature's API/field shape checks without unrelated hook helpers.
internal sealed record TeleportCopyCompatibility(bool Compatible, string Detail)
{
    public static unsafe TeleportCopyCompatibility Check()
    {
        try
        {
            foreach (var (type, name, expected) in new (Type, string, Type)[] {
                (typeof(AgentTeleport), "ContextMenuAetheryteIndex", typeof(byte)),
                (typeof(AgentTeleport), "AetheryteCount", typeof(int)),
                (typeof(AgentTeleport), "AetheryteList", typeof(StdVector<TeleportInfo>).MakePointerType()),
                (typeof(TeleportInfo), "AetheryteId", typeof(uint)),
                (typeof(TeleportInfo), "SubIndex", typeof(byte)),
                (typeof(TeleportInfo), "Ward", typeof(byte)),
                (typeof(TeleportInfo), "Plot", typeof(byte)) })
            {
                if (type.GetField(name)?.FieldType != expected) throw new MissingFieldException(type.Name, name);
            }
            return new(true, "APIのフィールド型を確認。実ゲームの構造互換性は別途確認が必要です。");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
}
