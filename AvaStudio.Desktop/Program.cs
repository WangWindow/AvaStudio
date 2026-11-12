using System;
using Avalonia;

namespace AvaStudio.Desktop;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if LINUX
            .UseWaylandWithFallback()
#elif WINDOWS
.With(new Win32PlatformOptions
{
    RenderingMode =
            [
                Win32RenderingMode.Vulkan,
                Win32RenderingMode.Software
            ],
    CompositionMode =
            [
                Win32CompositionMode.RedirectionSurface
            ]
})
#endif
            .WithInterFont()
            .LogToTrace();
}
