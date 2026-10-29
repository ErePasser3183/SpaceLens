namespace SpaceLens.Core;

public readonly record struct MapRect(double X, double Y, double Width, double Height);
public readonly record struct MapTile(MapItem Item, MapRect Bounds);

public static class TreemapLayout
{
    // Culling is a view operation, separate from the data tree and the requested detail depth.
    // Repack retained real items so omitted subpixel entries never become a giant empty bucket.
    public static List<MapTile> ArrangeVisible(IReadOnlyList<MapItem> input, MapRect bounds, Func<MapTile, bool> visible)
    {
        var retained = input.Where(item => item.Id >= 0 && item.Bytes > 0).OrderByDescending(item => item.Bytes).ToArray();
        while (retained.Length > 0)
        {
            var tiles = Arrange(retained, bounds);
            var next = tiles.Where(visible).Select(tile => tile.Item).ToArray();
            if (next.Length == retained.Length) return tiles;
            if (next.Length == 0)
            {
                if (retained.Length == 1) return [];
                // A dense directory can have no initially readable tile. Reduce the displayed set,
                // not the hierarchy depth; byte totals remain in the cached tree and folder label.
                retained = retained.Take(Math.Max(1, retained.Length / 2)).ToArray();
            }
            else retained = next;
        }
        return [];
    }
    // Squarified layout: preserve byte proportions while avoiding unreadable long strips.
    public static List<MapTile> Arrange(IReadOnlyList<MapItem> input, MapRect bounds)
    {
        var output = new List<MapTile>();
        if (bounds.Width <= 0 || bounds.Height <= 0) return output;
        var items = input.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToArray();
        double total = items.Sum(x => (double)x.Bytes);
        if (total <= 0) return output;
        double scale = bounds.Width * bounds.Height / total;
        int start = 0;
        var remaining = bounds;
        while (start < items.Length && remaining.Width > 0.0001 && remaining.Height > 0.0001)
        {
            int end = start + 1;
            double sum = items[start].Bytes * scale;
            double side = Math.Min(remaining.Width, remaining.Height);
            double worst = Worst(sum, items[start].Bytes * scale, items[start].Bytes * scale, side);
            while (end < items.Length)
            {
                double area = items[end].Bytes * scale;
                double next = Worst(sum + area, items[start].Bytes * scale, area, side);
                if (next > worst) break;
                worst = next; sum += area; end++;
            }
            bool vertical = remaining.Width >= remaining.Height;
            double thickness = sum / (vertical ? remaining.Height : remaining.Width);
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = items[i].Bytes * scale / thickness;
                var rect = vertical ? new MapRect(remaining.X, remaining.Y + offset, thickness, length)
                    : new MapRect(remaining.X + offset, remaining.Y, length, thickness);
                output.Add(new MapTile(items[i], rect)); offset += length;
            }
            remaining = vertical ? remaining with { X = remaining.X + thickness, Width = Math.Max(0, remaining.Width - thickness) }
                : remaining with { Y = remaining.Y + thickness, Height = Math.Max(0, remaining.Height - thickness) };
            start = end;
        }
        return output;
    }
    private static double Worst(double sum, double max, double min, double side) => Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
}
