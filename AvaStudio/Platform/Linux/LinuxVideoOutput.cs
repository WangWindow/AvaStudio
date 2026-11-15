#if LINUX
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaStudio.Platform.Linux;

/// <summary>
/// Linux video output is explicitly unavailable in this stage. The shell still gets a real,
/// readable Avalonia control; libmpv plays audio via <c>vo=null</c>. A future stage will add
/// either the render API or an X11 child window.
/// </summary>
public sealed class LinuxVideoOutput : IVideoOutput
{
    private readonly TextBlock _surface;

    public LinuxVideoOutput()
    {
        _surface = new TextBlock
        {
            Text = "当前平台暂不支持内嵌视频输出，仅播放音频。",
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
#endif
