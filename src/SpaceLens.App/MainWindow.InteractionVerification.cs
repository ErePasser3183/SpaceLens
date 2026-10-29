using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpaceLens.Core;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal async Task VerifyInteractionsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            var store = new NodeStore(); store.Add(@"D:\InteractionFixture", -1, 0, 0, NodeFlags.Directory);
            int a = store.Add("应用程序", 0, 0, 0, NodeFlags.Directory);
            int b = store.Add("资料", 0, 0, 0, NodeFlags.Directory);
            foreach (int parent in new[] { a, b })
                for (int branch = 0; branch < 5; branch++)
                {
                    int inner = store.Add("项目-" + branch, parent, 0, 0, NodeFlags.Directory);
                    int deep = store.Add("资源", inner, 0, 0, NodeFlags.Directory);
                    for (int folder = 0; folder < 2; folder++)
                    {
                        int fourth = store.Add("四级-" + folder, deep, 0, 0, NodeFlags.Directory);
                        for (int file = 0; file < 3; file++) store.Add("文件-" + file + ".bin", fourth, 1000 * (3 - file), 0, NodeFlags.None);
                    }
                }
            store.Aggregate();
            result = new ScanResult(store, [], 0, 0, false, TimeSpan.Zero, DateTime.UtcNow);
            mapDepth = 1; DepthText.Text = "1 层";
            ThemeManager.SetMode(AppThemeMode.Dark);
            await Refresh();
            var originalA = Map.BoundsFor(a); var originalB = Map.BoundsFor(b);
            var aPoint = Center(originalA); var bPoint = Center(originalB);
            double height = Map.ActualHeight;
            Map.UpdateHover(aPoint); Map.ShowHoverTip();
            Check(Map.HoverState.Open && Map.HoverState.Content?.ToString()?.Contains("应用程序") == true, "first hover shows matching folder");
            var oldTip = Map.HoverState;
            Map.UpdateHover(bPoint);
            Check(!Map.HoverState.Open && Map.HoverState.Content is null, "moving between tiles closes and clears the previous tooltip");
            Map.ShowHoverTip();
            Check(Map.HoverState.Open && Map.HoverState.Content?.ToString()?.Contains("资料") == true && (Map.HoverState.X != oldTip.X || Map.HoverState.Y != oldTip.Y), "new hover content is placed at the new pointer position");
            Map.HandlePrimaryClick(aPoint, 1);
            Check(!Map.HoverState.Open, "click dismisses hover tooltip");
            await Refresh();
            Check(current == 0 && Map.ExpandedDirectories.SetEquals(new[] { a }), "single click expands only its folder in place");
            Check(Map.BoundsFor(a) == originalA && Map.BoundsFor(b) == originalB, "local expansion preserves sibling rectangles");
            Check(FindName("DetailsPanel") is null && Map.ActualHeight == height, "selection never opens a bottom detail strip or shrinks the map");
            Map.HandlePrimaryClick(aPoint, 2); await Refresh();
            Check(current == a, "double click enters the original folder despite intervening expansion");
            Navigate(0); await Refresh();
            Check(expandedDepths.Count == 0, "navigation restores the global detail level");
            Map.HandlePrimaryClick(Center(Map.BoundsFor(a)), 1); await Refresh();
            var headerPoint = new Point(Map.BoundsFor(a).X + 20, Map.BoundsFor(a).Y + 6);
            Map.HandlePrimaryClick(headerPoint, 1); await Refresh();
            Check(current == 0 && !Map.ExpandedDirectories.Contains(a), "clicking folder header collapses the folder itself into a whole tile");
            await Refresh();
            Check(!Map.ExpandedDirectories.Contains(a) && Map.BoundsFor(a) == originalA && Map.BoundsFor(b) == originalB, "folder collapse survives refresh and preserves sibling areas");
            Map.HandlePrimaryClick(Center(Map.BoundsFor(a)), 1); await Refresh();
            Check(Map.ExpandedDirectories.Contains(a), "collapsed folder tile can be reopened with one click");
            int second = store.Children(a).First(), third = store.Children(second).First();
            Map.HandlePrimaryClick(Center(Map.BoundsFor(second)), 1); await Refresh();
            Map.HandlePrimaryClick(Center(Map.BoundsFor(third)), 1); await Refresh();
            Check(Map.ExpandedDirectories.Contains(third) && store.Children(third).All(id => Map.BoundsFor(id).Width > 0), "expanding level three shows both level-four folders");
            Map.HandlePrimaryClick(new Point(Map.BoundsFor(second).X + 20, Map.BoundsFor(second).Y + 6), 1); await Refresh();
            Check(Map.ExpandedDirectories.SetEquals(new[] { a }) && current == 0, "clicking nested header hides all its descendants and retains its parent");
            await Save("parent-restored.png");
            Map.HandlePrimaryClick(Center(Map.BoundsFor(second)), 1); await Refresh();
            Check(!Map.ExpandedDirectories.Contains(third), "reopening collapsed parent does not resurrect deeper expansion overrides");
            Map.HandlePrimaryClick(Center(Map.BoundsFor(third)), 1); await Refresh();
            Check(Map.ExpandedDirectories.Contains(third), "a collapsed child can be explicitly expanded again");
            MoreDepthClick(this, new RoutedEventArgs()); await Refresh();
            Check(mapDepth == 2 && expandedDepths.Count == 0 && Map.ExpandedDirectories.SetEquals(new[] { a, b }), "global plus resets local overrides and expands siblings uniformly");
            MoreDepthClick(this, new RoutedEventArgs()); await Refresh();
            Map.HandlePrimaryClick(new Point(Map.BoundsFor(a).X + 20, Map.BoundsFor(a).Y + 6), 1); await Refresh();
            Check(mapDepth == 3 && !Map.ExpandedDirectories.Contains(a) && store.Children(b).All(id => Map.ExpandedDirectories.Contains(id)), "header collapse overrides global depth without collapsing siblings");
            ResetExpansions(); await Refresh();
            headerPoint = new Point(Map.BoundsFor(a).X + 20, Map.BoundsFor(a).Y + 6);
            Map.HandlePrimaryClick(headerPoint, 1); await Refresh();
            Map.HandlePrimaryClick(headerPoint, 2); await Refresh();
            Check(current == a, "double clicking expanded header enters original folder after first-click collapse");
            Navigate(0); await Refresh();
            ResetExpansions(); await Refresh();
            Map.Select(-1); Map.ClearHover();
            await Save("dark-hierarchy.png");
            ThemeManager.SetMode(AppThemeMode.Light); await Map.WaitForRenderAsync();
            await Save("light-hierarchy.png");
            ThemeManager.SetMode(AppThemeMode.Dark);
            await Refresh();
            var p = new Point(Map.BoundsFor(a).X + 15, Map.BoundsFor(a).Y + 6);
            Map.UpdateHover(p); Map.ShowHoverTip();
            Map.SetItems([]);
            Check(!Map.HoverState.Open, "replacing or clearing the view closes stale tooltips");
            File.WriteAllText(Path.Combine(directory, "interaction-passed.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "interaction-failure.txt"), ex.ToString()); }
        finally { Close(); }
        async Task Refresh() { await UpdateViewAsync(); UpdateLayout(); await Map.WaitForRenderAsync(); }
        static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        async Task Save(string name)
        {
            await Map.WaitForRenderAsync();
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        }
    }
}
