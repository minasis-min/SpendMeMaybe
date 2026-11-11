using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;

namespace SpendMeMaybe;

public enum DisplayRule { Always, AtLeast, AtMost }
public enum VisibilityRule { Always, ShowAtLeast, HideAtLeast, ShowAtMost, HideAtMost }
public enum OverlayDirection { Down, Up, Right, Left }
public sealed record ItemInfo(uint Id, string Name, uint Icon, bool CanBeHq, bool IsCollectable, bool IsCurrency,
    CurrencyGroup Group = CurrencyGroup.Other, bool IsEventItem = false)
{
    public static ItemInfo? EventItem(uint id, string name, uint icon) => id == 0 || string.IsNullOrWhiteSpace(name)
        ? null : new(id, name, icon, false, false, false, IsEventItem: true);
}
public static class NativeHudVisibility
{
    // Absent/unreadable addons are unknown, not proof that HUD was hidden.
    // Logout and character initialization are handled by WorldUiVisibility.
    public static bool ShouldShow(bool? parameterVisible, bool chatExpanded, bool cutsceneActive) =>
        parameterVisible != false && !chatExpanded && !cutsceneActive;
}
public static class WorldUiVisibility
{
    public static bool CanDraw(bool loggedIn, bool playerLoaded, ulong contentId, ulong sampledContentId, bool hudVisible, bool gameUiHidden) =>
        loggedIn && playerLoaded && contentId != 0 && contentId == sampledContentId && hudVisible && !gameUiHidden;
}
public sealed class TrackedItem
{
    public Guid Id = Guid.NewGuid();
    public uint ItemId;
    public bool Enabled = true;
    public DisplayRule Rule;
    public int Threshold = 1000;
    public VisibilityRule Visibility;
    public int VisibilityThreshold = 1000;
    public bool Highlight = true;
    public bool IconOnly;
    public Vector4 WarningColor = new(1, .45f, .15f, 1);
    public string Command = "";
}
public sealed class Settings
{
    public int Version = 6;
    public int CommandHoldTimeoutSeconds = 60;
    public ReclickBehavior CommandReclickBehavior;
    public bool PositionIsAnchor;
    public bool Visible = true, Locked = false, ShowNames = false;
    public bool CountBeforeIcon;
    public OverlayDirection Direction;
    [JsonPropertyName("Horizontal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyHorizontal;
    public bool IconOnlyHorizontal;
    public bool TeleportCommandCopyEnabled;
    public TeleportCommandProvider TeleportCommandProvider;
    [JsonPropertyName("IconOnly"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyIconOnly;
    public float IconSize = 32, TextSize = 18, Spacing = 10, Background = .65f;
    public Vector2 Position = new(150, 150);
    public List<TrackedItem> Items = [];
    public Dictionary<string, float> SettingsColumnWidths = [];
    public bool SettingsSidebarCollapsed;

    public bool RestoreSizing(Settings defaults)
    {
        var iconChanged = RestoreSize(ref IconSize, defaults.IconSize);
        var textChanged = RestoreSize(ref TextSize, defaults.TextSize);
        var spacingChanged = RestoreSize(ref Spacing, defaults.Spacing);
        var backgroundChanged = RestoreSize(ref Background, defaults.Background);
        return iconChanged || textChanged || spacingChanged || backgroundChanged;
    }

    public static bool RestoreSize(ref float value, float defaultValue)
    {
        if (value == defaultValue) return false;
        value = defaultValue;
        return true;
    }
    public void Normalize()
    {
        SettingsColumnWidths ??= [];
        if (SettingsColumnWidths.Remove("registered", out var legacyWidth))
            SettingsColumnWidths.TryAdd("registered//registered", legacyWidth);
        foreach (var key in SettingsColumnWidths.Where(entry => string.IsNullOrWhiteSpace(entry.Key)
            || !float.IsFinite(entry.Value) || entry.Value <= 0).Select(entry => entry.Key).ToArray())
            SettingsColumnWidths.Remove(key);
        if (Version is not (1 or 2 or 3 or 4 or 5 or 6) || Items == null || Items.Any(x => x == null)) throw new InvalidDataException("未対応の設定形式");
        if (!Enum.IsDefined(CommandReclickBehavior)) throw new InvalidDataException("実行中のクリック設定が不正です");
        CommandHoldTimeoutSeconds = Math.Clamp(CommandHoldTimeoutSeconds, 5, 120);
        if (Version < 4) Direction = LegacyHorizontal == true ? OverlayDirection.Right : OverlayDirection.Down;
        if (!Enum.IsDefined(Direction)) throw new InvalidDataException("並べる方向が不正です");
        if (!Enum.IsDefined(TeleportCommandProvider)) throw new InvalidDataException("テレポコマンドの形式が不正です");
        if (Items.Count > 200) throw new InvalidDataException("登録件数が上限を超えています");
        IconSize = Limit(IconSize, 16, 96, 32); TextSize = Limit(TextSize, 10, 40, 18);
        Spacing = Limit(Spacing, 0, 60, 10); Background = Limit(Background, 0, 1, .65f);
        Position = new(Limit(Position.X, -30000, 30000, 150), Limit(Position.Y, -30000, 30000, 150));
        var ids = new HashSet<Guid>();
        foreach (var e in Items)
        {
            if (Version < 3) e.Visibility = VisibilityRule.Always;
            if (e.ItemId == 0 || !Enum.IsDefined(e.Rule) || !Enum.IsDefined(e.Visibility)) throw new InvalidDataException("登録項目が不正です");
            if (e.Id == Guid.Empty || !ids.Add(e.Id)) { e.Id = Guid.NewGuid(); ids.Add(e.Id); }
            e.Threshold = Math.Max(0, e.Threshold); e.Command ??= "";
            e.VisibilityThreshold = Math.Max(0, e.VisibilityThreshold);
            e.WarningColor = new(Limit(e.WarningColor.X, 0, 1, 1), Limit(e.WarningColor.Y, 0, 1, .45f), Limit(e.WarningColor.Z, 0, 1, .15f), Limit(e.WarningColor.W, .1f, 1, 1));
        }
        if (Version == 1 && LegacyIconOnly == true) foreach (var e in Items) e.IconOnly = true;
        LegacyIconOnly = null;
        LegacyHorizontal = null;
        Version = 6;
    }
    private static float Limit(float n, float min, float max, float fallback) => float.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
}
public readonly record struct CountReading(long? Count, string Reason = "")
{
    public string Text => Count?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "不明";
}
public static class Rules
{
    // Unknown is never treated as zero or hidden by an unverified condition.
    public static bool Shown(TrackedItem e, CountReading r, bool demoMode = false) => e.Enabled && (demoMode || r.Count is not { } count || e.Visibility switch
    {
        VisibilityRule.ShowAtLeast => count >= e.VisibilityThreshold,
        VisibilityRule.HideAtLeast => count < e.VisibilityThreshold,
        VisibilityRule.ShowAtMost => count <= e.VisibilityThreshold,
        VisibilityRule.HideAtMost => count > e.VisibilityThreshold,
        _ => true,
    });
    public static bool Reached(TrackedItem e, long n) => e.Rule switch { DisplayRule.AtLeast => n >= e.Threshold, DisplayRule.AtMost => n <= e.Threshold, _ => false };
    public static bool Highlighted(TrackedItem e, CountReading r) => e.Highlight && r.Count is { } count && Reached(e, count);
    public static string Label(Settings settings, TrackedItem item, string name, CountReading reading) => item.IconOnly ? "" : (settings.ShowNames ? name + "  " : "") + reading.Text;
    public static bool ValidCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var s = command.Trim();
        return s.StartsWith('/') && s.Length > 1 && s[1] != '/' && !char.IsWhiteSpace(s[1]) && !s.Any(char.IsControl) && Encoding.UTF8.GetByteCount(s) <= 500;
    }
}
// A release can consume its press once only. Movement is accumulated so dragging out and back also cancels.
public sealed class ClickGesture
{
    private Guid? pressed;
    private Vector2 origin;
    private bool dragged;
    public void Begin(Guid id, Vector2 mouse) { pressed = id; origin = mouse; dragged = false; }
    public void Move(Vector2 mouse, float threshold) { if (pressed != null && Vector2.DistanceSquared(origin, mouse) > threshold * threshold) dragged = true; }
    public bool Release(Guid id, bool hovered, bool locked)
    {
        var result = pressed == id && hovered && locked && !dragged;
        Cancel(); return result;
    }
    public void Cancel() { pressed = null; dragged = false; }
}
public sealed class OverlayMoveGesture
{
    private Vector2 startAnchor, startMouse;
    public bool Active { get; private set; }
    public bool TryBegin(bool demo, bool alt, bool windowHovered, bool titleHovered, bool leftPressed, Vector2 anchor, Vector2 mouse)
    {
        if (Active || !leftPressed || !windowHovered || !(alt || (demo && titleHovered))) return false;
        startAnchor = anchor; startMouse = mouse; Active = true;
        return true;
    }
    public bool Update(Vector2 mouse, bool leftDown, bool leftReleased, out Vector2 anchor)
    {
        anchor = default;
        if (!Active) return false;
        if (!leftDown && !leftReleased) { Cancel(); return false; }
        anchor = startAnchor + mouse - startMouse;
        if (leftReleased) Cancel();
        return true;
    }
    public void Cancel() => Active = false;
}
public static class RegisteredOrder
{
    public static bool Move(List<TrackedItem> items, Guid source, int target)
    {
        var index = items.FindIndex(e => e.Id == source);
        if (index < 0 || target < 0 || target >= items.Count || index == target) return false;
        var item = items[index];
        items.RemoveAt(index); items.Insert(target, item);
        return true;
    }
}
public static class OverlayAnchor
{
    public static Vector2 WindowPosition(Vector2 anchor, Vector2 minimum, Vector2 padding, float titleHeight) =>
        anchor + minimum - padding - new Vector2(0, titleHeight);
    public static Vector2 FromLegacyWindow(Vector2 window, Vector2 first, Vector2 minimum, Vector2 padding, float titleHeight) =>
        window + padding + new Vector2(0, titleHeight) + first - minimum;
}
public struct OverlayFlow(Vector2 origin, float gap, OverlayDirection direction, bool iconOnlyHorizontal)
{
    public OverlayFlow(Vector2 origin, float gap, bool horizontal, bool iconOnlyHorizontal)
        : this(origin, gap, horizontal ? OverlayDirection.Right : OverlayDirection.Down, iconOnlyHorizontal) { }
    private readonly float originX = origin.X;
    private readonly float originY = origin.Y;
    private Vector2 cursor = origin;
    private Vector2 previousSize;
    private float rowHeight;
    private bool started, previousIconOnly;
    public Vector2 Place(Vector2 size, bool iconOnly)
    {
        if (started)
        {
            if (direction is OverlayDirection.Left or OverlayDirection.Right || (iconOnlyHorizontal && iconOnly && previousIconOnly)) cursor.X += previousSize.X + gap;
            else { cursor = new(originX, cursor.Y + rowHeight + gap); rowHeight = 0; }
        }
        started = true; previousIconOnly = iconOnly; previousSize = size;
        rowHeight = Math.Max(rowHeight, size.Y);
        return direction switch
        {
            OverlayDirection.Up => new(cursor.X, 2 * originY - cursor.Y - size.Y),
            OverlayDirection.Left => new(2 * originX - cursor.X - size.X, cursor.Y),
            _ => cursor,
        };
    }
}
public static class CommandSequence
{
    public const int MaxLines = 5;
    public static bool TryParse(string? text, out string[] commands)
    {
        var valid = TryParseSteps(text, out var steps, out _);
        commands = valid ? steps.Select(step => step.Command).ToArray() : [];
        return valid;
    }
    public static bool TryParseSteps(string? text, out CommandStep[] steps, out string error)
    {
        steps = []; error = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length > MaxLines) { error = "コマンドは最大5件です。"; return false; }
        var parsed = new List<CommandStep>(lines.Length);
        foreach (var raw in lines)
        {
            if (Encoding.UTF8.GetByteCount(raw) > 500) { error = "入力行はwaitを含めUTF-8で500バイト以内です。"; return false; }
            var command = raw.Trim();
            var wait = 0;
            var start = -1;
            for (var open = command.IndexOf('<'); open >= 0; open = command.IndexOf('<', open + 1))
            {
                var word = open + 1;
                while (word < command.Length && char.IsWhiteSpace(command[word])) word++;
                if (command.AsSpan(word).StartsWith("wait", StringComparison.OrdinalIgnoreCase)) { start = open; break; }
            }
            if (start >= 0)
            {
                var suffix = command[start..];
                if (start == 0 || !char.IsWhiteSpace(command[start - 1]) || !suffix.StartsWith("<wait.", StringComparison.Ordinal)
                    || !suffix.EndsWith('>')) { error = "waitは行末に空白を挟んで <wait.1>〜<wait.60> と指定してください。"; return false; }
                var number = suffix[6..^1];
                if (number.Length is < 1 or > 2 || number[0] == '0' || number.Any(c => c is < '0' or > '9')
                    || !int.TryParse(number, out wait) || wait is < 1 or > 60)
                { error = "waitは小文字・整数1〜60秒で1つだけ指定してください。"; return false; }
                command = command[..start].TrimEnd();
            }
            var end = 0;
            while (end < command.Length && !char.IsWhiteSpace(command[end])) end++;
            var verb = command[..end];
            if (!Rules.ValidCommand(command) || verb.Equals("/wait", StringComparison.OrdinalIgnoreCase))
            { error = "各行は / で始めてください。/waitや//mには対応しません。"; return false; }
            parsed.Add(new(command, wait));
        }
        steps = parsed.ToArray();
        return true;
    }
}
public sealed class CommandRouter(Func<string, bool> pluginCommand, Action<string> gameCommand)
{
    public void Execute(string command)
    {
        if (!Rules.ValidCommand(command)) return;
        var text = command.Trim();
        if (!pluginCommand(text)) gameCommand(text);
    }
}
public static class CurrentTomestones
{
    // Game-defined roles: poetics, current uncapped, current weekly-capped.
    // Retired currencies have no active role. Item IDs/names are never hardcoded.
    public static uint[] Resolve(IEnumerable<(uint Role, uint ItemId)> rows) => rows
        .Where(x => x.Role is >= 1 and <= 3 && x.ItemId != 0)
        .OrderBy(x => x.Role).Select(x => x.ItemId).Distinct().ToArray();
}
public static class CurrencyKinds
{
    // Low-ID wallet balances are categorized as "Other" instead of "Currency".
    // Other filter groups cover the non-item wallet currencies shown in the currency UI.
    public static bool IsWalletCurrency(uint id, uint uiCategory, int filterGroup, uint stackSize) => id != 0 && stackSize > 1 &&
        (filterGroup is 29 or 47 or 55 or 56 or 58 || (id < 100 && uiCategory == 63 && filterGroup == 16));
}
public enum CurrencyGroup { Common, Battle, NonBattle, Tribal, Other }
public static class CurrencyGroups
{
    public static string Name(CurrencyGroup group) => group switch
    { CurrencyGroup.Common => "共通", CurrencyGroup.Battle => "戦闘", CurrencyGroup.NonBattle => "非戦闘", CurrencyGroup.Tribal => "友好部族", _ => "その他" };
    // Stable wallet IDs follow the in-game Currency tabs. Rotating tomestones and
    // society currencies are supplied from TomestonesItem and BeastTribe instead.
    public static CurrencyGroup Resolve(uint id, bool activeTomestone, bool tribalCurrency) =>
        tribalCurrency ? CurrencyGroup.Tribal : activeTomestone ? CurrencyGroup.Battle : id switch
        {
            1 or 20 or 21 or 22 or 29 or 21072 => CurrencyGroup.Common,
            25 or 27 or 10307 or 26533 or 26807 or 36656 => CurrencyGroup.Battle,
            10308 or 10309 or 10310 or 10311 or 17833 or 17834 or 25199 or 25200 or 33913 or 33914 or 41784 or 41785 or 28063 => CurrencyGroup.NonBattle,
            _ => CurrencyGroup.Other,
        };
}
