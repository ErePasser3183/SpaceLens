using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal async Task VerifyInputAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            SearchBox.Clear(); SearchBox.Focus(); UpdateLayout(); await Task.Delay(80);
            var origin = SearchPlaceholder.TranslatePoint(new Point(), SearchBox);
            var labelRect = SearchPlaceholder.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
            double labelCenter = origin.Y + labelRect.Y + labelRect.Height / 2;
            Save("empty-focused.png");
            SearchBox.Text = "文件名"; UpdateLayout(); await Task.Delay(80);
            var textRect = SearchBox.GetRectFromCharacterIndex(0);
            Save("text-focused.png");
            double difference = Math.Abs(textRect.Y + textRect.Height / 2 - labelCenter);
            File.WriteAllText(Path.Combine(directory, "input-metrics.json"), JsonSerializer.Serialize(new { labelCenter, textRect.Y, textRect.Height, difference, placeholderLeft = origin.X, textLeft = textRect.X }));
            if (difference > 1.5 || Math.Abs(origin.X - textRect.X) > 1.5)
                throw new InvalidOperationException($"placeholder differs from input: vertical {difference:F2}px, horizontal {Math.Abs(origin.X - textRect.X):F2}px");
            SearchBox.Clear(); UpdateLayout();
            if (SearchPlaceholder.Visibility != Visibility.Visible) throw new InvalidOperationException("placeholder does not return after clearing");
            File.WriteAllText(Path.Combine(directory, "input-passed.txt"), "Placeholder matches the text baseline and returns when cleared.");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "input-failure.txt"), ex.ToString()); }
        finally { Close(); }
        void Save(string name)
        {
            var visual = (FrameworkElement)SearchBox.Parent;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        }
    }
}
