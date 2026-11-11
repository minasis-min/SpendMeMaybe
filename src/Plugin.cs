using System.Diagnostics;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SpendMeMaybe;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/spendmemaybe";
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IGameGui game;
    private readonly ICondition conditions;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly SettingsStore store;
    private readonly Settings settings;
    private readonly ItemCatalog catalog;
    private readonly Views views;
    private readonly Func<CommandEnvironment> readCommandEnvironment;
    private readonly Action<string> sendCommand;
    private readonly NativeUiVisibility nativeUiVisibility;
    private TeleportCommandCopyFeature? teleportCopy;
    private bool hudVisible;
    private readonly CommandScheduler scheduler = new(() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private readonly object gate = new();
    private long nextRead;
    private ulong character;
    private bool registered, disposed;
    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IClientState client, IPlayerState player, IFramework framework,
        IDataManager data, ITextureProvider textures, IPluginLog log, IGameGui game, IContextMenu contextMenus, ICondition conditions)
    {
        this.pi = pi; this.commands = commands; this.client = client; this.player = player; this.framework = framework; this.log = log; this.game = game;
        this.conditions = conditions;
        nativeUiVisibility = new(log);
#if DISTRIBUTION
        var path = Path.Combine(pi.ConfigDirectory.FullName, "SpendMeMaybe.json");
#else
        var path = Path.Combine(pi.AssemblyLocation.DirectoryName!, "SpendMeMaybe.settings.json");
#endif
        store = new(path); settings = store.Load(); catalog = new(data);
        readCommandEnvironment = ExecutionEnvironment; sendCommand = new SlashCommandAction(commands).Execute;
        views = new(pi, textures, settings, store, catalog, Changed, Submit, scheduler, PositionChanged);
        try
        {
            teleportCopy = new(contextMenus, data, client, log, gate, () => settings.TeleportCommandCopyEnabled, () => settings.TeleportCommandProvider);
            registered = commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Spend Me Maybeの設定を開く" });
            if (!registered) throw new InvalidOperationException(Command + " は既に登録されています。");
            pi.UiBuilder.Draw += Draw;
            pi.UiBuilder.OpenConfigUi += Open;
            pi.UiBuilder.OpenMainUi += Open;
            pi.UiBuilder.HideUi += CancelGesture;
            framework.Update += Update;
        }
        catch { Dispose(); throw; }
    }
    private void OnCommand(string command, string arguments) => Open();
    private void Open() { lock (gate) views.ConfigOpen = true; }
    private void CancelGesture() { lock (gate) views.CancelGesture(); }
    private void PositionChanged() { store.QueueSave(Stopwatch.GetTimestamp()); scheduler.Cancel("設定変更"); }
    private void Changed() { PositionChanged(); nextRead = 0; views.Invalidate(); }
    private void Submit(TrackedItem item)
    {
        if (disposed || !CanDrawUi() || !settings.Locked || !item.Enabled) return;
        scheduler.Request(item.Id, catalog.Find(item.ItemId)?.Name ?? $"ID {item.ItemId}", item.Command,
            character, settings.CommandReclickBehavior, settings.CommandHoldTimeoutSeconds);
    }
    private CommandEnvironment ExecutionEnvironment()
    {
        var id = client.IsLoggedIn && player.IsLoaded && player.ContentId != 0 ? player.ContentId : character;
        // HUD visibility and native HP/MP visibility deliberately do not determine send readiness.
        var ready = client.IsLoggedIn && player.IsLoaded && player.ContentId != 0 && player.ContentId == character
            && !pi.UiBuilder.CutsceneActive
            && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51]
            && !conditions[ConditionFlag.WatchingCutscene] && !conditions[ConditionFlag.WatchingCutscene78]
            && !conditions[ConditionFlag.OccupiedInCutSceneEvent] && !conditions[ConditionFlag.OccupiedInQuestEvent]
            && !conditions[ConditionFlag.CreatingCharacter] && !conditions[ConditionFlag.LoggingOut];
        return new(client.IsLoggedIn, id, ready);
    }
    private bool CanDrawUi() => WorldUiVisibility.CanDraw(client.IsLoggedIn, player.IsLoaded, player.ContentId, character, hudVisible, game.GameUiHidden);
    private void Draw()
    {
        lock (gate)
        {
            if (disposed) return;
            if (!CanDrawUi()) { views.CancelGesture(); return; }
            views.Draw(true);
        }
    }
    private void Update(IFramework _) { lock (gate) UpdateState(); }
    private void UpdateState()
    {
        if (disposed) return;
        // Local player data may disappear while loading. Only logout or a known different ID cancels.
        var current = !client.IsLoggedIn ? 0 : player.IsLoaded && player.ContentId != 0 ? player.ContentId : character;
        if (current != character)
        {
            scheduler.Cancel(current == 0 ? "ログアウト" : "キャラクター変更");
            character = current; views.ClearReadings(); views.CancelGesture(); nextRead = 0;
        }
        var wasVisible = hudVisible;
        hudVisible = player.IsLoaded && current != 0 && !game.GameUiHidden && nativeUiVisibility.HudVisible(game, pi.UiBuilder.CutsceneActive);
        if (hudVisible && !wasVisible) nextRead = 0;
        if (!hudVisible) views.CancelGesture();
        try { if (scheduler.Active) scheduler.Tick(readCommandEnvironment, sendCommand); }
        catch (Exception ex) { scheduler.Cancel("実行時エラー"); log.Error(ex, "コマンド実行を中止しました。再送しません。"); }
        if (disposed) return;
        var now = Stopwatch.GetTimestamp();
        views.UpdateSaveStatus(store.SavePending(settings, now));
        if (!hudVisible || current == 0 || (!settings.Visible && !views.ConfigOpen) || settings.Items.Count == 0 || now < nextRead) return;
        nextRead = now + Stopwatch.Frequency / 2;
        foreach (var e in settings.Items)
        {
            if (!e.Enabled && !views.ConfigOpen) continue;
            var info = catalog.Find(e.ItemId);
            views.SetReading(e, info == null ? new(null, "ゲームデータに存在しない項目") : InventoryReader.Read(info));
        }
    }
    public void Dispose()
    {
        lock (gate) DisposeState();
    }
    private void DisposeState()
    {
        if (disposed) return;
        disposed = true;
        scheduler.Cancel("プラグイン無効化");

        pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenConfigUi -= Open; pi.UiBuilder.OpenMainUi -= Open;
        pi.UiBuilder.HideUi -= CancelGesture; framework.Update -= Update;
        try { teleportCopy?.Dispose(); }
        finally
        {
            try { if (registered) commands.RemoveHandler(Command); }
            finally { views.Dispose(); }
        }
    }
}


