using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaStudio.Platform.Unsupported;

/// <summary>
/// Explicit degradation for platforms without a video implementation. The surface is a real
/// Avalonia control so the shell can still present a coherent player area.
/// </summary>
public sealed class UnsupportedVideoOutput : IVideoOutput
{
    private readonly TextBlock _surface;

    public UnsupportedVideoOutput()
    {
        _surface = new TextBlock
        {
            Text = "当前平台不支持视频输出。",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
        };
    }

    public Control Surface => _surface;

    public long WindowId => 0;

    public bool IsAvailable => false;

    public event EventHandler? HandleCreated
    {
        add { }
        remove { }
    }

    public void Dispose()
    {
    }
}
