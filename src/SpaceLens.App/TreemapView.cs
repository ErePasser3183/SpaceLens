using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SpaceLens.Core;

namespace SpaceLens.App;

public sealed class TreemapView : FrameworkElement
{
    private readonly DrawingVisual drawing = new(), overlay = new();
    private readonly List<(Rect Rect, MapItem Item)> hitTargets = [];
    private IReadOnlyList<MapItem> items = [];
    private int hover = -2, selected = -2;
    private bool dark;
    private bool renderRequested, rendering;
    private Task? renderTask;
    private readonly LabelCache labels = new();
    private int contextKey, renderEpoch;
    private readonly ToolTip hoverTip = new() { Placement = PlacementMode.Relative, StaysOpen = true, IsHitTestVisible = false };
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private Point hoverPoint;
    private MapItem? clickOrigin;
    internal int RenderedFrames { get; private set; }
    internal double LastRenderMilliseconds { get; private set; }
    internal int SkippedUnchangedFrames { get; private set; }
    public bool HideTinyTiles { get; private set; } = true;
    internal int HiddenTileCount { get; private set; }
    internal int LabelledTileCount { get; private set; }
    internal int UnlabelledTileCount { get; private set; }
    internal IReadOnlySet<int> ExpandedDirectories { get; private set; } = new HashSet<int>();
    public void SetHideTinyTiles(bool value) { HideTinyTiles = value; Redraw(); }
    public bool ShowNested { get; set; } = true;
    public event Action<int>? Selected;
    public event Action<int>? Entered;
    public event Action<int, int>? ExpandRequested;
    public event Action<int, Point>? ContextRequested;
    private static readonly Typeface Typeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly SolidColorBrush TextBrush = Brush("#29433F"), SubBrush = Brush("#506966");
    private static readonly Dictionary<string, SolidColorBrush> Palette = new()
    {
        ["视频"] = Brush("#AFCEE1"), ["音频"] = Brush("#D5C2DF"), ["图片"] = Brush("#E8D3A7"),
        ["文档"] = Brush("#B8D9D4"), ["程序"] = Brush("#B7D2C6"), ["归档"] = Brush("#DBBFB3"),
        ["代码"] = Brush("#BAC8E4"), ["其他"] = Brush("#CCD6E2"), ["文件夹"] = Brush("#C3DDD3")
    };
    private static readonly SolidColorBrush[] DirectoryPalette = [Brush("#B5D5C7"), Brush("#BFD3E2"), Brush("#D5C9DE"), Brush("#DCD1B5"), Brush("#B8D8D6")];
    private static readonly Dictionary<string, SolidColorBrush> DarkPalette = new()
    {
        ["视频"] = Brush("#345B76"), ["音频"] = Brush("#5A4569"), ["图片"] = Brush("#685A3C"),
        ["文档"] = Brush("#365B5A"), ["程序"] = Brush("#3D5D50"), ["归档"] = Brush("#655044"),
        ["代码"] = Brush("#435674"), ["其他"] = Brush("#485668"), ["文件夹"] = Brush("#3E5D53")
    };
    private static readonly SolidColorBrush[] DarkDirectories = [Brush("#3A594E"), Brush("#354F65"), Brush("#53465F"), Brush("#5D543E"), Brush("#365957")];
    private static readonly SolidColorBrush DarkText = Brush("#E1EAE9"), DarkSub = Brush("#B8CCC7");
    public void SetDark(bool value) { dark = value; Redraw(); }
    public TreemapView()
    {
        AddVisualChild(drawing); AddVisualChild(overlay);
        hoverTip.PlacementTarget = this;
        hoverTimer.Tick += (_, _) => ShowHoverTip();
        Unloaded += (_, _) => ClearHover();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearHover(); };
        SizeChanged += (_, _) => Redraw();
        MouseMove += (_, e) => UpdateHover(e.GetPosition(this));
        MouseLeave += (_, _) => { ClearHover(); DrawOverlay(); };
        MouseLeftButtonDown += (_, e) => HandlePrimaryClick(e.GetPosition(this), e.ClickCount);
        MouseRightButtonDown += (_, e) =>
        {
            ClearHover();
            if (Hit(e.GetPosition(this)) is not { } h || h.Item.Id < 0) return;
            selected = h.Item.Id; Selected?.Invoke(selected); DrawOverlay(); ContextRequested?.Invoke(selected, e.GetPosition(this));
        };
    }
    internal void UpdateHover(Point point)
    {
        hoverPoint = point;
        int id = Hit(point)?.Item.Id ?? -2;
        if (id == hover) return;
        ClearHover();
        hover = id;
        Cursor = id >= 0 ? Cursors.Hand : Cursors.Arrow;
        if (id >= 0) hoverTimer.Start();
        DrawOverlay();
    }
    internal void ShowHoverTip()
    {
        hoverTimer.Stop();
        if (Hit(hoverPoint) is not { } hit || hit.Item.Id != hover || hover < 0) return;
        hoverTip.Content = hit.Item.DetailText ?? $"{hit.Item.Name}\n{DisplayFormat.Size(hit.Item.Bytes)}";
        hoverTip.HorizontalOffset = hoverPoint.X + 12;
        hoverTip.VerticalOffset = hoverPoint.Y + 20;
        hoverTip.IsOpen = true;
    }
    internal void ClearHover()
    {
        hoverTimer.Stop(); hoverTip.IsOpen = false; hoverTip.Content = null; hover = -2;
    }
    internal (bool Open, object? Content, double X, double Y) HoverState => (hoverTip.IsOpen, hoverTip.Content, hoverTip.HorizontalOffset, hoverTip.VerticalOffset);
    internal Rect BoundsFor(int id) => hitTargets.First(h => h.Item.Id == id).Rect;
    internal void HandlePrimaryClick(Point point, int clickCount)
    {
        ClearHover();
        // Expanding the first click can move the children under the pointer before the second click.
        // Keep the original target so a double click always enters that directory.
        if (clickCount == 2 && clickOrigin is { Directory: true } origin)
        { Selected?.Invoke(origin.Id); Entered?.Invoke(origin.Id); clickOrigin = null; return; }
        if (Hit(point) is not { } hit || hit.Item.Id < 0) return;
        clickOrigin = hit.Item;
        selected = hit.Item.Id; Selected?.Invoke(selected); DrawOverlay();
        if (clickCount == 1 && hit.Item.Directory)
        {
            bool header = ExpandedDirectories.Contains(selected) && point.Y < hit.Rect.Top + 14;
            ExpandRequested?.Invoke(selected, header ? 1 : 2);
        }
    }
    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? drawing : overlay;
    protected override HitTestResult HitTestCore(PointHitTestParameters parameters) => new PointHitTestResult(this, parameters.HitPoint);
    public void SetItems(IReadOnlyList<MapItem> value, int context = 0)
    {
        int comparisonBudget = 2048;
        if (contextKey == context && SameVisibleData(items, value, Math.Max(ActualWidth, ActualHeight), ref comparisonBudget)) { SkippedUnchangedFrames++; return; }
        if (contextKey != context || value.Count == 0) { contextKey = context; renderEpoch++; }
        ClearHover(); items = value; selected = -2; hitTargets.Clear(); DrawOverlay();
        if (value.Count == 0) { using var dc = drawing.RenderOpen(); }
        Redraw();
    }
    private static bool SameVisibleData(IReadOnlyList<MapItem> before, IReadOnlyList<MapItem> after, double dimension, ref int remaining)
    {
        if (before.Count != after.Count) return false;
        double totalBefore = Math.Max(1, before.Sum(item => (double)item.Bytes)), totalAfter = Math.Max(1, after.Sum(item => (double)item.Bytes));
        double prefixBefore = 0, prefixAfter = 0;
        for (int i = 0; i < before.Count; i++)
        {
            if (--remaining < 0) return false;
            var a = before[i]; var b = after[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Directory != b.Directory || a.Category != b.Category || a.ValueText != b.ValueText || a.DetailText != b.DetailText || CompactSize(a.Bytes) != CompactSize(b.Bytes)) return false;
            prefixBefore += a.Bytes; prefixAfter += b.Bytes;
            if (Math.Abs(prefixBefore / totalBefore - prefixAfter / totalAfter) * dimension > .15) return false;
            if ((a.Children is null) != (b.Children is null)) return false;
            if (a.Children is not null && !SameVisibleData(a.Children, b.Children!, dimension, ref remaining)) return false;
        }
        return true;
    }
    public void Select(int id) { selected = id; DrawOverlay(); }
    private (Rect Rect, MapItem Item)? Hit(Point p)
    {
        for (int i = hitTargets.Count - 1; i >= 0; i--) if (hitTargets[i].Rect.Contains(p)) return hitTargets[i];
        return null;
    }
    private void Redraw()
    {
        renderRequested = true;
        if (!rendering) renderTask = RenderLoopAsync();
    }
    internal async Task WaitForRenderAsync()
    {
        while (renderTask is { } task && !task.IsCompleted) await task;
    }
    private async Task RenderLoopAsync()
    {
        rendering = true;
        try
        {
            while (renderRequested)
            {
                renderRequested = false;
                var source = items; var size = RenderSize; bool isDark = dark, hide = HideTinyTiles, nested = ShowNested;
                int epoch = renderEpoch;
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var frame = await Task.Run(() => BuildFrame(source, size, isDark, hide, nested, dpi));
                // Publish completed frames within the same view even if newer data is waiting.
                // Discarding every superseded frame can starve the display during a fast scan.
                if (epoch != renderEpoch || size != RenderSize || isDark != dark || hide != HideTinyTiles) continue;
                using (var dc = drawing.RenderOpen()) dc.DrawDrawing(frame.Drawing);
                hitTargets.Clear(); hitTargets.AddRange(frame.Hits);
                HiddenTileCount = frame.Hidden; LabelledTileCount = frame.Labelled; UnlabelledTileCount = frame.Unlabelled;
                ExpandedDirectories = frame.Expanded;
                LastRenderMilliseconds = frame.Elapsed; RenderedFrames++;
                DrawOverlay();
            }
        }
        finally { rendering = false; }
    }
    private sealed record Frame(DrawingGroup Drawing, List<(Rect Rect, MapItem Item)> Hits, int Hidden, int Labelled, int Unlabelled, double Elapsed, HashSet<int> Expanded);
    private Frame BuildFrame(IReadOnlyList<MapItem> sourceItems, Size size, bool dark, bool hideTiny, bool showNested, double dpi)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var group = new DrawingGroup();
        var targets = new List<(Rect Rect, MapItem Item)>();
        int hidden = 0, labelled = 0, unlabelled = 0;
        var expanded = new HashSet<int>();
        using (var dc = group.Open())
        {
        // Subpixel children retain their area in the parent; don't turn them into a dense grid.
        dc.DrawRectangle(sourceItems.Count > 0 ? Brush(dark ? "#364752" : "#C8D8E3") : Brushes.Transparent, null, new Rect(size));
        var palette = dark ? DarkPalette : Palette;
        var textBrush = dark ? DarkText : TextBrush;
        var subBrush = dark ? DarkSub : SubBrush;
        var folder = Brush(dark ? "#364752" : "#C8D8E3");
        var header = Brush(dark ? "#435561" : "#DDE7EE");
        var edge = new Pen(Brush(dark ? "#697B87" : "#92A9BA"), 0.65);
        edge.Freeze();
        var smallEdge = new Pen(Brush(dark ? "#495F6D" : "#B4C7D4"), 0.45);
        smallEdge.Freeze();
        var directoryEdge = new Pen(Brush(dark ? "#92A6B4" : "#6D899D"), 1.1);
        directoryEdge.Freeze();
        bool CanLabel(string name, double width, double height)
        {
            if (name.Length == 0 || width < 4 || height < 9.5) return false;
            var first = labels.Get(StringInfo.GetNextTextElement(name), LabelSize(width, height), textBrush, dpi);
            return width >= first.Width + 1.3 && height >= first.Height - .5;
        }
        void Text(DrawingContext context, string text, double x, double y, double width, double fontSize, Brush brush, bool center = false)
        {
            if (width <= 0) return;
            var run = labels.Get(text, fontSize, brush, dpi);
            double offset = center && run.Width <= width ? (width - run.Width) / 2 : 0;
            context.PushClip(new RectangleGeometry(new Rect(x, y, width, Math.Ceiling(run.Height))));
            context.PushTransform(new TranslateTransform(x + offset, y));
            context.DrawDrawing(run.Drawing); context.Pop(); context.Pop();
        }
        void DrawTree(IReadOnlyList<MapItem> source, MapRect area)
        {
            var tiles = TreemapLayout.ArrangeVisible(source, area,
                tile => hideTiny ? CanLabel(tile.Item.Name, tile.Bounds.Width - .8, tile.Bounds.Height - .8) : tile.Bounds.Width >= 1.8 && tile.Bounds.Height >= 1.8);
            hidden += source.Count - tiles.Count;
            foreach (var tile in tiles)
            {
                if (tile.Item.Id < 0) continue;
                var r = Inset(tile.Bounds, 0.4);
                if (r.Width < 1 || r.Height < 1) continue;
                var item = tile.Item;
                bool canLabel = CanLabel(item.Name, r.Width, r.Height);
                if (hideTiny && !canLabel) { hidden++; continue; }
                uint colorKey = 0;
                if (item.Category.StartsWith("进程:", StringComparison.Ordinal))
                    foreach (char character in item.Category) colorKey = unchecked(colorKey * 31 + character);
                var fill = item.Category.StartsWith("进程:", StringComparison.Ordinal)
                    ? (dark ? DarkDirectories : DirectoryPalette)[colorKey % (uint)DirectoryPalette.Length]
                    : item.Directory ? folder : palette.GetValueOrDefault(item.Category, palette["其他"]);
                dc.DrawRectangle(fill, item.Directory ? directoryEdge : Math.Min(r.Width, r.Height) < 24 ? smallEdge : edge, r);
                targets.Add((r, item));
                bool nested = showNested && item.Directory && item.Children is { Count: > 0 };
                var childArea = new MapRect(r.X + 1.5, r.Y + 14, r.Width - 3, Math.Max(0, r.Height - 15.5));
                if (nested)
                {
                    expanded.Add(item.Id);
                    dc.PushClip(new RectangleGeometry(r));
                    dc.DrawRectangle(header, null, new Rect(r.X + .3, r.Y + .3, r.Width - .6, Math.Min(r.Height, 13)));
                    Text(dc, item.Name + " · " + (item.ValueText ?? CompactSize(item.Bytes)), r.X + .65, r.Y + .3, r.Width - 1.3, Math.Min(10, LabelSize(r.Width, r.Height)), textBrush);
                    labelled++;
                    if (childArea.Height > 0 && childArea.Width > 0) DrawTree(item.Children!, childArea);
                    dc.Pop();
                    dc.DrawRectangle(null, directoryEdge, r);
                }
                else if (canLabel)
                {
                    double fontSize = LabelSize(r.Width, r.Height);
                    double lineHeight = fontSize * 1.25;
                    bool twoLines = r.Height >= lineHeight * 2 + 1 && r.Width >= 17;
                    double y = r.Y + Math.Max(.2, (r.Height - lineHeight * (twoLines ? 2 : 1)) / 2);
                    string label = !twoLines && r.Width > 120 ? item.Name + " · " + (item.ValueText ?? CompactSize(item.Bytes)) : item.Name;
                    dc.PushClip(new RectangleGeometry(r));
                    Text(dc, label, r.X + .65, y, r.Width - 1.3, fontSize, textBrush, true);
                    if (twoLines) Text(dc, item.ValueText ?? CompactSize(item.Bytes), r.X + .65, y + lineHeight, r.Width - 1.3, Math.Min(10, fontSize), subBrush, true);
                    dc.Pop(); labelled++;
                }
                else unlabelled++;
            }
        }
        DrawTree(sourceItems, new MapRect(0, 0, size.Width, size.Height));
        }
        group.Freeze();
        return new Frame(group, targets, hidden, labelled, unlabelled, clock.Elapsed.TotalMilliseconds, expanded);
    }
    private static double LabelSize(double width, double height) => height < 13 || width < 22 ? 8 : width < 40 ? 9 : 10.5;
    private static string CompactSize(long bytes) => DisplayFormat.Size(bytes).Replace(" KiB", "K").Replace(" MiB", "M").Replace(" GiB", "G").Replace(" TiB", "T").Replace(" B", "B");
    private void DrawOverlay()
    {
        using var dc = overlay.RenderOpen();
        foreach (var h in hitTargets)
            if (h.Item.Id >= 0 && (h.Item.Id == selected || h.Item.Id == hover))
                dc.DrawRectangle(null, new Pen(Brush(dark ? "#8EC7F0" : h.Item.Id == selected ? "#28658F" : "#6F94AF"), h.Item.Id == selected ? 2 : 1), h.Rect);
    }
    private sealed record TextRun(string Text, double Size, Brush Brush, double Dpi, DrawingGroup Drawing, double Width, double Height);
    private sealed class LabelCache
    {
        // Reuse shaped glyphs; fixed capacity prevents unbounded growth while browsing millions of names.
        private readonly TextRun?[] cache = new TextRun?[8192];
        public TextRun Get(string text, double size, Brush brush, double dpi)
        {
            int slot = HashCode.Combine(text, size, brush, dpi) & (cache.Length - 1);
            var cached = cache[slot];
            if (cached is not null && cached.Text == text && cached.Size == size && cached.Brush == brush && cached.Dpi == dpi) return cached;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface, size, brush, dpi) { MaxLineCount = 1 };
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open()) dc.DrawText(formatted, new Point());
            drawing.Freeze();
            return cache[slot] = new TextRun(text, size, brush, dpi, drawing, formatted.WidthIncludingTrailingWhitespace, formatted.Height);
        }
    }
    private static Rect Inset(MapRect r, double pad) => new(r.X + pad, r.Y + pad, Math.Max(0, r.Width - 2 * pad), Math.Max(0, r.Height - 2 * pad));
    private static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
}
