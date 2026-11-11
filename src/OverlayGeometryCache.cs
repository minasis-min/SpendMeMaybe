using System.Numerics;

namespace SpendMeMaybe;

public readonly record struct OverlayMeasureRow(string Label, bool IconOnly);
public readonly record struct OverlayLayoutStyle(float Scale, float FontSize, int FontKey,
    float IconSize, float TextSize, float Spacing, OverlayDirection Direction, bool HorizontalIcons, bool CountBeforeIcon = false);
public readonly record struct OverlayPlacement(Vector2 Position, Vector2 Size, Vector2 TextSize);

/// <summary>Owned by the rendering view. Invalidate after changing rows in place; never stores native pointers or texture IDs.</summary>
public sealed class OverlayGeometryCache
{
    private readonly List<OverlayPlacement> placements = [];
    private IReadOnlyList<OverlayMeasureRow>? previousRows;
    private OverlayLayoutStyle previousStyle;
    private bool dirty = true;
    public IReadOnlyList<OverlayPlacement> Placements => placements;
    public Vector2 Minimum { get; private set; }
    public Vector2 Maximum { get; private set; }
    public Vector2 First { get; private set; }
    public void Invalidate() => dirty = true;

    public void Update(IReadOnlyList<OverlayMeasureRow> rows, OverlayLayoutStyle style, Func<string, Vector2> measure)
    {
        if (!dirty && ReferenceEquals(previousRows, rows) && previousStyle == style) return;
        dirty = true;
        placements.Clear();
        var flow = new OverlayFlow(Vector2.Zero, style.Spacing * style.Scale, style.Direction, style.HorizontalIcons);
        var alignRight = style.CountBeforeIcon && style.Direction is OverlayDirection.Down or OverlayDirection.Up;
        var firstWidth = 0f;
        var factor = style.TextSize * style.Scale / style.FontSize;
        var icon = style.IconSize * style.Scale;
        var minimum = Vector2.Zero; var maximum = Vector2.Zero; var first = Vector2.Zero;
        foreach (var row in rows)
        {
            var textSize = row.Label.Length == 0 ? Vector2.Zero : measure(row.Label) * factor;
            var size = row.Label.Length == 0 ? new Vector2(icon)
                : new Vector2(icon + 8 * style.Scale + textSize.X, Math.Max(icon, textSize.Y));
            var position = flow.Place(size, row.IconOnly);
            if (placements.Count == 0)
            {
                first = position;
                firstWidth = size.X;
            }
            if (alignRight) position.X += firstWidth - size.X;
            placements.Add(new(position, size, textSize));
            minimum = Vector2.Min(minimum, position); maximum = Vector2.Max(maximum, position + size);
        }
        Minimum = minimum; Maximum = maximum; First = first;
        previousRows = rows; previousStyle = style; dirty = false;
    }
}
