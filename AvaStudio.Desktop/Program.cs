using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using AvaStudio.Services.Shell;
using Microsoft.Win32;

namespace AvaStudio.Desktop;

internal sealed class Program
{
    /// <summary>崩溃日志文件名。</summary>
    private const string CrashLogName = "crash.log";

    /// <summary>与 MSI 开始菜单快捷方式一致的 AppUserModelID，系统媒体卡片据此显示应用名/图标。</summary>
    private const string AppUserModelId = "AvaStudio";

    /// <summary>给未打包 Win32 应用设置显式 AUMID；失败不影响启动。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            RegisterAppUserModelId();
        }

        // 已有实例时只转发命令行并退出；首个实例监听后续打开请求。
        if (!SingleInstanceService.Initialize())
        {
            _ = SingleInstanceService.SendToPrimary(args);
            return;
        }

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

    /// <summary>
    /// 未打包 Win32 应用也需要 DisplayName/IconUri 才能让系统媒体卡片显示应用名，
    /// 而不是“未知应用”；MSI 安装时同样写入该 HKCU 键并在卸载时清理。
    /// </summary>
    private static void RegisterAppUserModelId()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{AppUserModelId}");
            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", "AvaStudio", RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                key.SetValue("IconUri", executablePath, RegistryValueKind.String);
            }
        }
        catch
        {
            // 系统媒体卡片名称是增强信息，失败不影响播放与启动。
        }
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
