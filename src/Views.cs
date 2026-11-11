using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MinasisUI;
using MinasisUI.Workspace;
using Dalamud.Interface.Windowing;
using CommandRole = SoftCharcoalFlowUiKit.CommandRole;

namespace SpendMeMaybe;

public sealed class Views : IDisposable
{
    private static WorkspaceDefinition Definition => new("SpendMeMaybe.Settings", "Spend Me Maybe 設定", "Spend Me\nMaybe",
        $"Ver {typeof(Views).Assembly.GetName().Version?.ToString(3)}",
        [
            Page("registered", "貨幣設定", FontAwesomeIcon.Coins),
            Page("add", "貨幣を追加", FontAwesomeIcon.Plus),
            new("layout", "レイアウト", FontAwesomeIcon.PaintBrush, [new("placement", "配置"), new("size", "スタイル")]),
            new("extras", "追加機能", FontAwesomeIcon.PuzzlePiece, [new("teleport", "テレポ"), new("commands", "コマンド")])
        ]);
    private static WorkspacePage Page(string id, string label, FontAwesomeIcon icon) => new(id, label, icon, []);
    private readonly WorkspaceUi workspace;
    private readonly WorkspaceListState registeredList = new() { AllowReorder = true, CheckboxAfterHandle = true };
    private readonly List<WorkspaceListItem> registeredItems = [];
    private long? pendingUiRevision;
    private readonly WindowSystem windows = new("SpendMeMaybe");
    private readonly WorkspaceWindow window;
    private readonly WorkspaceFonts fonts;
    private readonly IFontHandle axisFont;
    private static readonly UiChoice[] OverlayFontChoices = [new("default", "Dalamud標準"), new("axis", "FF14 Axis")];
    private static readonly Vector2[] OutlineDirections =
    [
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
        new(-.7071f, -.7071f), new(.7071f, -.7071f), new(-.7071f, .7071f), new(.7071f, .7071f)
    ];
    private readonly WorkspaceUi[] surfaces;
    private Ui ui => workspace.Controls;
    private static readonly Settings Defaults = new();
    private readonly ITextureProvider textures;
    private readonly Settings settings;
    private readonly SettingsStore store;
    private readonly ItemCatalog catalog;
    private readonly Action changed;
    private readonly Action positionChanged;
    private readonly Action drawSettings;
    private readonly Action<TrackedItem> submit;
    private readonly CommandScheduler scheduler;
    private readonly Dictionary<Guid, CountReading> readings = [];
    private readonly ClickGesture gesture = new();
    private readonly record struct OverlayRow(TrackedItem Entry, ItemInfo? Info, CountReading Count, string Key, string Name, string Label);
    private readonly List<OverlayRow> overlayRows = [];
    private readonly List<OverlayMeasureRow> measureRows = [];
    private readonly OverlayGeometryCache geometry = new();
    private static readonly Func<string, Vector2> MeasureLabel = static text => ImGui.CalcTextSize(text);
    private bool rowsDirty = true, registeredDirty = true, disposed;
    private static readonly UiChoice[] RuleChoices = [new("always", "条件なし"), new("above", "指定数以上"), new("below", "指定数以下")];
    private static readonly UiChoice[] VisibilityChoices = [new("always", "常に表示"), new("show-above", "指定数以上で表示"), new("hide-above", "指定数以上で非表示"), new("show-below", "指定数以下で表示"), new("hide-below", "指定数以下で非表示")];
    private static readonly UiChoice[] DirectionChoices = [new("down", "上から下"), new("up", "下から上"), new("right", "左から右"), new("left", "右から左")];
    private static readonly UiChoice[] ProviderChoices = [new("teleporter", "Teleporter"), new("lifestream", "Life Stream")];
    private static readonly UiChoice[] ReclickChoices = [new("ignore", "無視する"), new("cancel", "現在の実行を中止する"), new("restart", "現在の実行を中止し、クリック先を実行する")];
    private Guid? selected;
    private Guid? commandEditorItem;
    private string commandEditorId = "";
    private CommandInputState commandEditor = new();
    private string search = "";
    private string? checkedCommands;
    private bool commandsValid;
    private string commandError = "";
    private List<ItemInfo> results = [];
    private int selectedToken;
    private UiChoiceGroup[]? tokenGroups;
    private readonly OverlayMoveGesture overlayMove = new();
    public bool ConfigOpen
    {
        get => window.IsOpen;
        set => window.IsOpen = workspace.IsOpen = value;
    }
    public Views(IDalamudPluginInterface pi, ITextureProvider textures, Settings settings, SettingsStore store, ItemCatalog catalog, Action changed, Action<TrackedItem> submit, CommandScheduler scheduler, Action? positionChanged = null)
    {
        this.textures = textures; this.settings = settings; this.store = store; this.catalog = catalog; this.changed = changed; this.submit = submit;
        this.scheduler = scheduler;
        this.positionChanged = positionChanged ?? changed;
        drawSettings = windows.Draw;
        workspace = new(Definition, new WorkspaceState { ExpandedSize = new(900, 630), IsOpen = false },
            new WorkspacePreferences
            {
                SidebarCollapsed = settings.SettingsSidebarCollapsed,
                ColumnWidths = new(settings.SettingsColumnWidths)
            });
        workspace.Persistence.SaveRequested += SaveUiPreferences;
        try
        {
            fonts = new(pi);
            axisFont = pi.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 18));
            window = new(workspace, DrawSettingsContent);
            surfaces = [workspace];
            windows.AddWindow(window);
        }
        catch
        {
            workspace.Persistence.SaveRequested -= SaveUiPreferences;
            try { windows.RemoveAllWindows(); }
            finally { UiResourceDisposal.DisposeAll([workspace, fonts, axisFont]); }
            throw;
        }
    }
    private void SaveUiPreferences(WorkspaceSaveRequest request)
    {
        settings.SettingsSidebarCollapsed = request.Preferences.SidebarCollapsed;
        settings.SettingsColumnWidths = new(request.Preferences.ColumnWidths);
        pendingUiRevision = request.Revision;
        store.QueueSave(System.Diagnostics.Stopwatch.GetTimestamp());
        UpdateSaveStatus(false);
    }

    public void UpdateSaveStatus(bool saved)
    {
        if (pendingUiRevision is not { } revision) return;
        if (saved)
        {
            workspace.Persistence.AcknowledgeSaved(revision);
            pendingUiRevision = null;
        }
        else if (store.Notice.Length > 0)
            workspace.Persistence.ReportSaveFailure(revision, store.Notice);
    }
    public void SetReading(TrackedItem item, CountReading reading)
    {
        var id = item.Id;
        if (!readings.TryGetValue(id, out var old) || old != reading)
        {
            if (Rules.Shown(item, old, !settings.Locked) != Rules.Shown(item, reading, !settings.Locked)) gesture.Cancel();
            readings[id] = reading; rowsDirty = registeredDirty = true;
        }
    }
    public void ClearReadings() { readings.Clear(); rowsDirty = registeredDirty = true; }
    public void Invalidate() { rowsDirty = registeredDirty = true; gesture.Cancel(); }
    public void CancelGesture() { gesture.Cancel(); overlayMove.Cancel(); }
    private CountReading Reading(Guid id) => readings.GetValueOrDefault(id, new(null, "所持数の読み込み待ち"));
    public void Draw(bool loggedIn)
    {
        if (settings.Visible && loggedIn) DrawOverlay(); else CancelGesture();
        if (ConfigOpen) fonts.Draw(surfaces, drawSettings);
    }
    private void DrawSettingsContent()
    {
        var page = workspace.State.PageId;
        if (store.Notice.Length > 0) workspace.Flow.Text(store.Notice);
        if (page == "registered" && settings.Items.Count > 0 && workspace.AvailableWidth >= 520)
        {
            workspace.Columns("registered", 184, float.MaxValue, RegisteredList, RegisteredDetail);
            return;
        }

        workspace.PageScrollPane(page, () =>
        {
            switch (page)
            {
                case "registered":
                    if (settings.Items.Count > 0)
                        workspace.Card("registered-list", "登録一覧", 280, RegisteredList);
                    RegisteredDetail();
                    break;
                case "add": Search(); break;
                case "layout": Appearance(); break;
                case "extras": Extras(); break;
            }
        });
    }

    private void RegisteredList()
    {
        if (registeredDirty)
        {
            registeredItems.Clear();

            foreach (var item in settings.Items)
            {
                var info = catalog.Find(item.ItemId);
                var name = info?.Name ?? $"ID {item.ItemId}";
                var texture = info == null ? null : textures.GetFromGameIcon(new GameIconLookup(info.Icon));
                registeredItems.Add(new(item.Id.ToString(), $"{name}  {Reading(item.Id).Text}")
                {
                    SharedTexture = texture,
                    Enabled = item.Enabled
                });
            }
            registeredDirty = false;
        }
        if (selected is { } desired)
        {
            if (!Guid.TryParse(registeredList.SelectedId, out var existing) || existing != desired)
                registeredList.SelectedId = desired.ToString();
        }
        else registeredList.SelectedId = null;
        var result = workspace.SearchableList("registered-items", registeredItems, registeredList, "登録した貨幣を検索");
        selected = Guid.TryParse(registeredList.SelectedId, out var selection) ? selection : null;
        if (result.ToggledId is { } toggled && result.Enabled is { } enabled
            && Guid.TryParse(toggled, out var toggledId)
            && settings.Items.Find(item => item.Id == toggledId) is { } entry)
        {
            entry.Enabled = enabled;
            changed();
        }
        if (result.Move is { } move && Guid.TryParse(move.SourceId, out var source)
            && Guid.TryParse(move.TargetId, out var target))
        {
            var sourceIndex = settings.Items.FindIndex(item => item.Id == source);
            var targetIndex = settings.Items.FindIndex(item => item.Id == target);
            var destination = targetIndex + (move.AfterTarget ? 1 : 0);
            if (sourceIndex < destination) destination--;
            if (sourceIndex >= 0 && targetIndex >= 0 && RegisteredOrder.Move(settings.Items, source, destination))
                changed();
        }
    }
    private void RegisteredDetail()
    {
        if (settings.Items.Count == 0) { workspace.Flow.Text("「貨幣を追加」から登録してください。"); return; }
        var e = settings.Items.Find(x => x.Id == selected) ?? settings.Items[0];
        selected = e.Id;
        var info = catalog.Find(e.ItemId);
        workspace.SelectionContext(registeredList, info?.Name ?? $"ID {e.ItemId}");
        workspace.Card("display", "", 200, () =>
        {
            var enabled = e.Enabled ? 0 : 1;
            if (ui.TwoOptionSwitch("item-enabled-" + e.Id, "有効", "無効", ref enabled))
            {
                e.Enabled = enabled == 0;
                changed();
            }
            if (info != null) { workspace.TextureIcon(ItemIcon(info), 32); workspace.SameLine(); }
            workspace.HeadingText(info?.Name ?? $"不明な項目 ID {e.ItemId}", false);
            workspace.Flow.Muted($"所持数: {Reading(e.Id).Text}");
            if (ui.Checkbox("item-icon-only", "アイコンだけ表示", ref e.IconOnly)) changed();
            int visibility = (int)e.Visibility;
            if (ui.Dropdown("visibility", "表示条件", ref visibility, VisibilityChoices)) { e.Visibility = (VisibilityRule)visibility; changed(); }
            if (e.Visibility != VisibilityRule.Always && ui.InputInt("visibility-threshold", "基準数", ref e.VisibilityThreshold, step: 1, min: 0, max: int.MaxValue)) changed();
            workspace.Flow.Muted("以上・以下は同数を含みます。所持数が不明なら表示します。");
        });
        workspace.Card("color", "文字色", 160, () =>
        {
            if (ui.Checkbox("highlight", "条件に応じて色を変える", ref e.Highlight, enabled: !e.IconOnly)) changed();
            if (!e.Highlight) return;
            int rule = (int)e.Rule;
            if (ui.Dropdown("rule", "色を変える条件", ref rule, RuleChoices, enabled: !e.IconOnly)) { e.Rule = (DisplayRule)rule; changed(); }
            if (e.Rule == DisplayRule.Always) return;
            if (ui.InputInt("threshold", "基準数", ref e.Threshold, step: 1, enabled: !e.IconOnly, min: 0, max: int.MaxValue)) changed();
            if (ui.ColorInput("color", "文字色", ref e.WarningColor, enabled: !e.IconOnly)) changed();
        });

        if (commandEditorItem != e.Id)
        {
            commandEditorItem = e.Id;
            commandEditorId = e.Id.ToString();
            commandEditor = new();
        }
        workspace.Card("click", "クリック", 240, () =>
        {
            if (ui.InputCommands(commandEditorId, "コマンド（最大5行）", ref e.Command, commandEditor, height: 110, maxLength: 3000)) changed();
            workspace.Flow.Muted("行末の <wait.1>〜<wait.60> で送信後に待機。処理の成功・完了は確認しません。");
            workspace.Flow.Muted("例：/tp エーテライト名（Teleporter導入時）");
            if (checkedCommands != e.Command) { checkedCommands = e.Command; commandsValid = CommandSequence.TryParseSteps(e.Command, out _, out commandError); }
            if (!string.IsNullOrWhiteSpace(e.Command) && !commandsValid) workspace.Flow.Text(commandError);
        });
        workspace.Card("registration", "登録管理", 100, () =>
        {
            if (workspace.Flow.Command("remove", "登録解除", CommandRole.Destructive))
            {
                settings.Items.Remove(e);
                readings.Remove(e.Id);
                selected = null;
                changed();
            }
        });
    }

    private void Search()
    {
        workspace.Card("tokens", "通貨リスト", 140, () =>
        {
            var tokens = catalog.CurrentTokens;
            if (tokens.Count > 0)
            {
                tokenGroups ??= Enum.GetValues<CurrencyGroup>().Select(group => new UiChoiceGroup(CurrencyGroups.Name(group),
                    tokens.Where(token => token.Group == group).Select(token => new UiChoice(token.Id.ToString(), token.Name)).ToArray())).ToArray();
                var token = tokens[selectedToken];
                var key = token.Id.ToString();
                if (ui.GroupedDropdown("current-token", "通貨", token.Name, ref key, tokenGroups))
                {
                    var itemId = uint.Parse(key);
                    for (var i = 0; i < tokens.Count; i++)
                    {
                        if (tokens[i].Id != itemId) continue;
                        selectedToken = i;
                        token = tokens[i];
                        break;
                    }
                }
                workspace.TextureIcon(ItemIcon(token), 32);
                workspace.SameLine();
                var exists = settings.Items.Any(x => x.ItemId == token.Id);
                if (workspace.Flow.Command("add-token", exists ? "登録済み" : "追加", CommandRole.Primary, enabled: !exists && settings.Items.Count < 200))
                    AddItem(token);
            }
            if (catalog.TokenError.Length > 0) workspace.Flow.Text(catalog.TokenError);
        });

        workspace.Card("name-search", "名前で検索", 150, () =>
        {
            workspace.Flow.Muted("日本語名の一部で検索。旧貨幣・通常アイテム・だいじなものも追加できます。");
            if (ui.SearchInput("search", ref search, "日本語名を入力", maxLength: 100))
            {
                var query = search.Trim();
                results = query.Length == 0 ? [] : catalog.All
                    .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Id).ToList();
            }
            if (catalog.Error.Length > 0) workspace.Flow.Text(catalog.Error);
            workspace.Flow.Muted($"検索結果: {results.Count:N0}件（先頭200件） / 登録: {settings.Items.Count}/200件");
        });
        foreach (var result in results.Take(200))
        {
            workspace.TextureIcon(ItemIcon(result), 24);
            workspace.SameLine();
            var exists = settings.Items.Any(x => x.ItemId == result.Id);
            if (workspace.Flow.Command("add-" + result.Id, $"{result.Name}  [ID {result.Id}]" + (exists ? " 登録済" : " 追加"),
                enabled: !exists && settings.Items.Count < 200))
                AddItem(result);
        }
    }

    private void AddItem(ItemInfo info)
    {
        if (settings.Items.Count >= 200 || settings.Items.Any(x => x.ItemId == info.Id)) return;
        var item = new TrackedItem { ItemId = info.Id };
        settings.Items.Add(item); selected = item.Id; changed();
    }
    private void Appearance()
    {
        if (workspace.State.TabId == "size")
        {
            workspace.Card("text", "文字", 340, DrawTextStyle, ResetTextStyle);
            workspace.Card("icon", "アイコン", 120, DrawIconStyle);
            workspace.Card("spacing", "間隔", 120, DrawSpacingStyle);
            workspace.Card("background", "背景", 120, DrawBackgroundStyle);
            return;
        }
        workspace.Card("layout", "配置と表示", 400, DrawPlacement);
        workspace.Card("inventory", "集計対象", 100, () =>
            workspace.Flow.Muted("本人のバッグ内NQ＋HQ、貨幣、だいじなもの。装備・収集品・アーマリー・サドルバッグ・リテイナー・FCは含みません。"));
    }

    private void DrawPlacement()
    {
        if (ui.Checkbox("visible", "オーバーレイを表示", ref settings.Visible)) changed();
        if (ui.Checkbox("names", "貨幣名を表示", ref settings.ShowNames)) changed();
        if (ui.Checkbox("count-before-icon", "所持数をアイコンの左に表示", ref settings.CountBeforeIcon)) changed();
        workspace.Flow.Muted("貨幣名は「アイコンだけ表示」の項目には表示しません。");
        ui.SectionHeader("配置", "");
        var demo = !settings.Locked;
        if (ui.Checkbox("demo", "Demo（位置調整）", ref demo)) { settings.Locked = !demo; changed(); }
        workspace.Flow.Muted(demo ? "有効な貨幣をすべて表示。タイトルバーをドラッグして移動できます。" : "通常表示：表示条件を適用し、左クリックでコマンドを実行できます。");
        workspace.Flow.Muted("Alt＋ドラッグなら、通常表示でも移動できます。");
        var direction = (int)settings.Direction;
        if (ui.RadioGroup("direction", "並べる方向", ref direction, DirectionChoices))
        {
            settings.Direction = (OverlayDirection)direction;
            changed();
        }
        if (ui.Checkbox("icon-only-horizontal", "アイコンだけの項目を横に並べる", ref settings.IconOnlyHorizontal, enabled: settings.Direction is OverlayDirection.Down or OverlayDirection.Up)) changed();
        workspace.Flow.Muted("上下方向でも、連続するアイコンは同じ行に並べます。");
        if (workspace.Flow.Command("reset-position", "位置を画面左上に戻す"))
        {
            var position = ImGui.GetMainViewport().Pos + new Vector2(100, 100);
            if (settings.PositionIsAnchor && settings.Position == position) return;
            settings.Position = position;
            settings.PositionIsAnchor = true;
            overlayMove.Cancel();
            changed();
        }
    }

    private void DrawTextStyle()
    {
        if (ui.Dropdown("font", "フォント", ref settings.TextFont, OverlayFontChoices)) changed();
        ui.Field("textsize", "文字サイズ",
            () => { if (ui.InputFloat("value", "", ref settings.TextSize, step: 1, min: 10, max: 40)) changed(); },
            new() { Unit = "px", Reset = () => { if (Settings.RestoreSize(ref settings.TextSize, Defaults.TextSize)) changed(); } });
        if (ui.Checkbox("outline", "文字に縁取りを付ける", ref settings.TextOutline)) changed();
        if (ui.Slider("outline-strength", "縁取りの強さ", ref settings.OutlineStrength, .1f, 3, "%.1f", enabled: settings.TextOutline)) changed();
        if (ui.ColorInput("outline-color", "縁取りの色", ref settings.OutlineColor, enabled: settings.TextOutline)) changed();
    }

    private void ResetTextStyle() { if (settings.RestoreTextStyle(Defaults)) changed(); }

    private void DrawIconStyle() =>
        ui.Field("iconsize", "アイコンサイズ",
            () => { if (ui.InputFloat("value", "", ref settings.IconSize, step: 1, min: 16, max: 96)) changed(); },
            new() { Unit = "px", Reset = () => { if (Settings.RestoreSize(ref settings.IconSize, Defaults.IconSize)) changed(); } });

    private void DrawSpacingStyle() =>
        ui.Field("spacing", "項目の間隔",
            () => { if (ui.InputFloat("value", "", ref settings.Spacing, step: 1, min: 0, max: 60)) changed(); },
            new() { Unit = "px", Reset = () => { if (Settings.RestoreSize(ref settings.Spacing, Defaults.Spacing)) changed(); } });

    private void DrawBackgroundStyle() =>
        ui.Field("background", "背景の濃さ", () =>
        {
            var opacity = settings.Background * 100;
            if (ui.InputFloat("value", "", ref opacity, step: 5, min: 0, max: 100)) { settings.Background = opacity / 100; changed(); }
        }, new() { Unit = "%", Reset = () => { if (Settings.RestoreSize(ref settings.Background, Defaults.Background)) changed(); } });

    private void Extras()
    {
        if (workspace.State.TabId == "commands")
            workspace.Card("command-run", "コマンド実行", 320, DrawCommandOptions);
        else workspace.Card("teleport", "テレポコマンド", 250, DrawTeleportOptions);
    }

    private void DrawTeleportOptions()
    {
        if (ui.Checkbox("teleport-copy-enabled", "テレポコマンドをコピーする", ref settings.TeleportCommandCopyEnabled)) changed();
        ui.Field("teleport-options", "", () =>
        {
            workspace.Flow.Muted("テレポ一覧からエーテライト名を右クリックしてサブコマンド「コマンドをコピー」を選び、クリップボードにコピーします。コマンドはLife StreamとTeleporterから選択できます。");
            workspace.Flow.Muted("コピーだけではテレポしません。実行には選択したプラグインが必要です。Teleporterの使用言語はゲームと揃えてください。");
            workspace.Flow.Muted("共有ハウス・アパルトメントなど、名前だけで場所を指定できない候補はコピーできません。");
            var provider = (int)settings.TeleportCommandProvider;
            if (ui.RadioGroup("teleport-provider", "使用するプラグイン", ref provider, ProviderChoices))
            {
                settings.TeleportCommandProvider = (TeleportCommandProvider)provider;
                changed();
            }
        }, new() { Enabled = settings.TeleportCommandCopyEnabled });
    }

    private void DrawCommandOptions()
    {
        if (workspace.Flow.Command("cancel-command-run", "実行を中止", enabled: scheduler.Active)) scheduler.Cancel("手動中止");
        if (ui.InputInt("command-hold-timeout", "保留タイムアウト（秒）", ref settings.CommandHoldTimeoutSeconds, step: 1, min: 5, max: 120)) changed();
        workspace.Flow.Muted("読み込み・ムービー中は保留し、復帰後0.5秒の確認で再開します。wait時間は保留に含みません。");
        var behavior = (int)settings.CommandReclickBehavior;
        if (ui.RadioGroup("reclick", "実行中のアイコンクリック", ref behavior, ReclickChoices))
        {
            settings.CommandReclickBehavior = (ReclickBehavior)behavior;
            changed();
        }
        workspace.Flow.Muted("切り替えても、すでに送ったコマンドは取り消せません。");
    }
    private ImTextureID? ItemIcon(ItemInfo info)
        => textures.GetFromGameIcon(new GameIconLookup(info.Icon)).GetWrapOrDefault()?.Handle;

    private void DrawOverlay()
    {
        if (rowsDirty)
        {
            overlayRows.Clear();
            measureRows.Clear();
            geometry.Invalidate();
            foreach (var e in settings.Items)
            {
                var count = Reading(e.Id);
                // Keep the saved anchor, not an invisible row or its dimensions.
                if (!Rules.Shown(e, count, !settings.Locked)) continue;
                var info = catalog.Find(e.ItemId);
                var name = info?.Name ?? $"ID {e.ItemId}";
                var label = Rules.Label(settings, e, name, count);
                overlayRows.Add(new(e, info, count, e.Id.ToString(), name, label));
                measureRows.Add(new(label, e.IconOnly));
            }
            rowsDirty = false;
        }
        var hasVisible = overlayRows.Count > 0;
        if (!hasVisible && settings.Locked) { CancelGesture(); return; }
        var movingThisFrame = overlayMove.Active;
        if (overlayMove.Update(ImGui.GetMousePos(), ImGui.IsMouseDown(ImGuiMouseButton.Left), ImGui.IsMouseReleased(ImGuiMouseButton.Left), out var movedAnchor)
            && movedAnchor != settings.Position)
        {
            settings.Position = movedAnchor;
            positionChanged();
        }
        var scale = ImGuiHelpers.GlobalScale;
        using var selectedFont = settings.TextFont == 1 && axisFont.Available ? axisFont.Lock() : null;
        var textFont = selectedFont?.ImFont ?? default;
        var textSizePx = settings.TextSize * scale;
        using (workspace.Flow.PushFont(textFont))
        {
            var fontSize = ImGui.GetFontSize();
            geometry.Update(measureRows, new(scale, fontSize, ImGui.GetFont().GetHashCode(),
                settings.IconSize, settings.TextSize, settings.Spacing, settings.Direction, settings.IconOnlyHorizontal, settings.CountBeforeIcon), MeasureLabel);
        }
        var iconSize = settings.IconSize * scale;
        var outlineRadius = settings.TextOutline && settings.OutlineColor.W > 0 ? settings.OutlineStrength * scale : 0;
        var outlineColor = ImGui.ColorConvertFloat4ToU32(settings.OutlineColor);
        var placements = geometry.Placements;
        var minimum = geometry.Minimum;
        var maximum = geometry.Maximum;
        var first = geometry.First;
        var padding = ImGui.GetStyle().WindowPadding;
        var titleHeight = settings.Locked ? 0 : ImGui.GetFontSize() + 2 * ImGui.GetStyle().FramePadding.Y;
        if (!settings.PositionIsAnchor)
        {
            settings.Position = OverlayAnchor.FromLegacyWindow(settings.Position, first, minimum, padding, titleHeight);
            settings.PositionIsAnchor = true;
            positionChanged();
        }
        minimum -= first; maximum -= first;
        if (!hasVisible) { minimum = Vector2.Zero; maximum = new(300 * scale, 30 * scale); }
        if (outlineRadius > 0 && hasVisible)
        {
            minimum -= new Vector2(outlineRadius);
            maximum += new Vector2(outlineRadius);
        }
        ImGui.SetNextWindowPos(OverlayAnchor.WindowPosition(settings.Position, minimum, padding, titleHeight), ImGuiCond.Always);
        var windowSize = maximum - minimum + padding * 2 + new Vector2(0, titleHeight);
        ImGui.SetNextWindowSize(Vector2.Max(windowSize, ImGui.GetStyle().WindowMinSize), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(settings.Background);
        var flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        if (settings.Locked) flags |= ImGuiWindowFlags.NoTitleBar;
        var visible = ImGui.Begin("Spend Me Maybe（Demo・位置調整）###SpendMeMaybe.Overlay", flags);
        Guid hovered = Guid.Empty;
        try
        {
            if (!visible) { CancelGesture(); return; }
            var openSettings = ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (openSettings)
            {
                ConfigOpen = true;
                gesture.Cancel();
            }
            var windowPosition = ImGui.GetWindowPos();
            // The title bar lies outside the content clip rectangle: do not clip this hit test.
            var overTitle = titleHeight > 0 && ImGui.IsMouseHoveringRect(windowPosition,
                windowPosition + new Vector2(ImGui.GetWindowSize().X, titleHeight), false);
            if (overlayMove.TryBegin(!settings.Locked, ImGui.GetIO().KeyAlt,
                ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem), overTitle,
                ImGui.IsMouseClicked(ImGuiMouseButton.Left), settings.Position, ImGui.GetMousePos()))
            {
                movingThisFrame = true;
            }
            if (movingThisFrame || ImGui.GetIO().KeyAlt) gesture.Cancel();
            var contentOrigin = ImGui.GetCursorScreenPos();
            // Use the actual content origin in case the host adjusted window decoration.
            var origin = contentOrigin - minimum;
            gesture.Move(ImGui.GetMousePos(), 5 * scale);
            for (var index = 0; index < overlayRows.Count; index++)
            {
                var row = overlayRows[index];
                var e = row.Entry; var count = row.Count;
                var info = row.Info;
                var (position, size, textSize) = placements[index];
                var cursor = origin + position - first;
                if (openSettings && ImGui.IsMouseHoveringRect(cursor, cursor + size))
                {
                    selected = e.Id;
                    registeredList.Search = "";
                    workspace.Select("registered");
                }
                var reversed = settings.CountBeforeIcon && row.Label.Length > 0;
                var iconPosition = cursor + new Vector2(reversed ? textSize.X + 8 * scale : 0, 0);
                var textPosition = cursor + new Vector2(reversed ? 0 : iconSize + 8 * scale, (size.Y - textSize.Y) / 2);
                ImGui.SetCursorScreenPos(iconPosition);
                ImGui.InvisibleButton(row.Key, new Vector2(iconSize));
                var over = ImGui.IsItemHovered();
                if (over)
                {
                    hovered = e.Id;
                    ui.Scale = scale;
                    ui.Tooltip(() =>
                    {
                        ImGui.TextUnformatted(row.Name); ImGui.TextUnformatted("所持数: " + count.Text);
                        if (count.Count == null) ImGui.TextUnformatted(count.Reason);
                        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(e.Command) ? "コマンド: 未設定" : "コマンド: " + e.Command);
                        if (!settings.Locked) ImGui.TextUnformatted("Demo中はコマンドを実行しません。");
                        ImGui.TextUnformatted("Alt＋ドラッグで移動できます。");
                        ImGui.TextUnformatted("右クリックで設定を開きます。");
                        DrawExecutionStatus();
                    });
                    if (settings.Locked && !movingThisFrame && !ImGui.GetIO().KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) gesture.Begin(e.Id, ImGui.GetMousePos());
                }
                var draw = ImGui.GetWindowDrawList();
                var wrap = info == null ? null : textures.GetFromGameIcon(new GameIconLookup(info.Icon)).GetWrapOrDefault();
                if (wrap != null) draw.AddImage(wrap.Handle, iconPosition, iconPosition + new Vector2(iconSize));
                else draw.AddText(iconPosition + new Vector2(iconSize * .35f, iconSize * .2f), 0xffffffff, "?");
                var color = Rules.Highlighted(e, count) ? e.WarningColor : Vector4.One;
                if (row.Label.Length > 0)
                {
                    using var labelFont = workspace.Flow.PushFont(textFont);
                    var font = ImGui.GetFont();
                    if (outlineRadius > 0)
                        foreach (var direction in OutlineDirections)
                            draw.AddText(font, textSizePx, textPosition + direction * outlineRadius, outlineColor, row.Label);
                    draw.AddText(font, textSizePx, textPosition, ImGui.ColorConvertFloat4ToU32(color), row.Label);
                }
                ImGui.SetCursorScreenPos(cursor); ImGui.Dummy(size);
            }
            if (!hasVisible && !settings.Locked) ImGui.TextUnformatted("表示する貨幣がありません — 右クリックで設定");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                if (gesture.Release(hovered, hovered != Guid.Empty, settings.Locked && !movingThisFrame && !ImGui.GetIO().KeyAlt))
                {
                    var e = settings.Items.FirstOrDefault(x => x.Id == hovered);
                    if (e != null) submit(e);
                }
            }
            else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) gesture.Cancel();
        }
        finally { ImGui.End(); }
    }
    private void DrawExecutionStatus()
    {
        var status = scheduler.Status();
        if (status.State == CommandRunState.Idle) return;
        ImGui.Separator();
        ImGui.TextUnformatted((scheduler.Active ? "実行中の貨幣: " : "直前の貨幣: ") + status.Currency);
        var description = status.State switch
        {
            CommandRunState.Sending => "送信待ち",
            CommandRunState.Waiting => "wait中",
            CommandRunState.Holding => "保留中",
            CommandRunState.Stabilizing => "再開確認中",
            CommandRunState.Completed => "完了",
            _ => "中止: " + status.Reason,
        };
        ImGui.TextUnformatted("実行状態: " + description);
        var label = status.State switch { CommandRunState.Waiting => "wait残り", CommandRunState.Holding => "保留タイムアウトまで", CommandRunState.Stabilizing => "再開確認の残り", _ => "" };
        if (label.Length > 0) ImGui.TextUnformatted($"{label}: {Math.Ceiling(status.RemainingSeconds * 10) / 10:0.0}秒");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelGesture();
        try { windows.RemoveAllWindows(); }
        finally
        {
            try { workspace.Dispose(); }
            finally
            {
                workspace.Persistence.SaveRequested -= SaveUiPreferences;
                try { UpdateSaveStatus(store.SavePending(settings, System.Diagnostics.Stopwatch.GetTimestamp(), flush: true)); }
                finally { UiResourceDisposal.DisposeAll([fonts, axisFont]); }
            }
        }
    }
}
