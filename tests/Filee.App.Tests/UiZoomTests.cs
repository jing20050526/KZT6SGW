// Ctrl + wheel, Ctrl + plus / minus and Ctrl + 0 zoom every window.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Services;

namespace Filee.App.Tests;

public class UiZoomTests
{
    [AvaloniaFact]
    public void Ctrl_and_the_wheel_or_plus_minus_zoom_every_window()
    {
        TestServices.EnsureInitialized("en");
        var window = new Window { Content = new Border { Width = 100, Height = 100 }, Width = 300, Height = 300 };
        UiZoom.Track(window);
        window.Show();
        try
        {
            UiZoom.Set(1, remember: false);
            var scaled = Assert.IsType<LayoutTransformControl>(window.Content);
            window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            Assert.Equal(1.1, Assert.IsType<ScaleTransform>(scaled.LayoutTransform).ScaleX, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(1.2, UiZoom.Scale, 6);
            window.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal(1, UiZoom.Scale, 6);
            Assert.Null(scaled.LayoutTransform);
            UiZoom.Set(99, remember: false);
            Assert.Equal(UiZoom.Max, UiZoom.Scale);
        }
        finally
        {
            UiZoom.Set(1, remember: false);
            window.Close();
        }
    }
}
