// Adapted from Minasis Tweaks (OrbitMovement), MIT, Copyright 2026 Minasis.
namespace SpendMeMaybe;

public static class TeleportMenuColor
{
    public const uint TargetRgba = 0x2EE3DCFF;
    public static bool TrySelect(IEnumerable<(uint Id, uint Rgba)> colors, out ushort id)
    {
        id = 0;
        var best = int.MaxValue;
        foreach (var color in colors)
        {
            if (color.Id is 0 or > ushort.MaxValue || (color.Rgba & 0xFF) != 0xFF) continue;
            var r = (int)(color.Rgba >> 24) - 0x2E;
            var g = (int)((color.Rgba >> 16) & 0xFF) - 0xE3;
            var b = (int)((color.Rgba >> 8) & 0xFF) - 0xDC;
            var distance = r * r + g * g + b * b;
            if (distance < best || (distance == best && color.Id < id)) { best = distance; id = (ushort)color.Id; }
        }
        return id != 0;
    }
}

public enum TeleportCommandProvider { Teleporter, Lifestream }
public readonly record struct TeleportCommandDestination(uint Id, byte SubIndex, byte Ward, byte Plot, string Name, string AethernetName)
{
    public bool SameLocation(TeleportCommandDestination other) => Id == other.Id && SubIndex == other.SubIndex && Ward == other.Ward && Plot == other.Plot;
}

public static class TeleportCommandCopy
{
    public static bool TryBuild(TeleportCommandProvider provider, TeleportCommandDestination selected,
        IReadOnlyList<TeleportCommandDestination> candidates, out string command, out string reason)
    {
        command = ""; reason = "";
        if (!Enum.IsDefined(provider)) { reason = "コピー形式を選び直してください"; return false; }
        var name = selected.Name?.Trim() ?? "";
        if (selected.Id == 0 || string.IsNullOrWhiteSpace(name) || name.Length > 300 || name.Any(char.IsControl))
        { reason = "行き先名を取得できません"; return false; }
        // Neither name-based command distinguishes wards/plots or apartment subindices.
        if (selected.SubIndex != 0 || selected.Ward != 0 || selected.Plot != 0)
        { reason = "この住宅の行き先は名前だけでは指定できません"; return false; }
        if (!candidates.Any(x => x.SameLocation(selected))) { reason = "行き先一覧を開き直してください"; return false; }
        if (provider == TeleportCommandProvider.Lifestream && name.IndexOfAny([',', '@']) >= 0)
        { reason = "この行き先名はLifestreamのコマンドに使用できません"; return false; }
        // The displayed order may differ from a provider's unlocked list. Require every
        // matching location to be the selected one, even with partial-name matching.
        var matches = new List<TeleportCommandDestination>();
        if (provider == TeleportCommandProvider.Lifestream)
            foreach (var candidate in candidates)
                if (!string.IsNullOrEmpty(candidate.AethernetName) && candidate.AethernetName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
        foreach (var candidate in candidates)
            if (!string.IsNullOrEmpty(candidate.Name) && candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        if (matches.Count == 0 || matches.Any(match => !match.SameLocation(selected)))
        { reason = "同名・部分一致する別の行き先があるためコピーできません"; return false; }
        command = (provider == TeleportCommandProvider.Teleporter ? "/tp " : "/li tp ") + name;
        return true;
    }
}
