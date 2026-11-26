using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LolConfigSync.App.Controls;

/// <summary>A window using the themed chrome (custom title bar with minimize/close).</summary>
public class HexWindow : Window
{
    /// <summary>Dev aid: when set, each window saves a PNG of itself here after it first renders.</summary>
    public const string SnapshotDirVariable = "LOLCONFIGSYNC_SNAPSHOT_DIR";

    public HexWindow()
    {
        SetResourceReference(StyleProperty, "HexWindowStyle");
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        ContentRendered += (_, _) => SaveSnapshotIfRequested();
    }

    private void SaveSnapshotIfRequested() => SaveSnapshotIfRequested(Title.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Dev aid: saves <c>{WindowType}-{suffix}.png</c> when the snapshot variable is set; otherwise does nothing.</summary>
    protected void SaveSnapshotIfRequested(string suffix)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotDirVariable);
        if (string.IsNullOrWhiteSpace(dir) || Content is not FrameworkElement) return;
        Dispatcher.BeginInvoke(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(dir);
            using var file = File.Create(Path.Combine(dir, $"{GetType().Name}-{suffix}.png"));
            encoder.Save(file);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
