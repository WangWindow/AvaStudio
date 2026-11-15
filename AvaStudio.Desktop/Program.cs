using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;

namespace AvaStudio.Desktop;

internal sealed class Program
{
    /// <summary>崩溃日志文件名。</summary>
    private const string CrashLogName = "crash.log";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if LINUX
            .UseWaylandWithFallback()
#endif
            .WithInterFont()
            .LogToTrace();
    }

    /// <summary>把未处理异常写入本机日志，便于定位 UI 线程/XAML 异常。</summary>
    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AvaStudio",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, CrashLogName);
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能掩盖原始异常。
        }
    }
}
