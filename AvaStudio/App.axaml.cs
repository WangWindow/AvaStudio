using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaStudio.Services.Library;
using AvaStudio.Services.Shell;
using AvaStudio.ViewModels;
using AvaStudio.Views;

namespace AvaStudio;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    /// <summary>待打开的外部媒体路径（来自资源管理器/命令行），只在主窗口首次打开后消费一次。</summary>
    private string? _pendingExternalMediaPath;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    /// <summary>记录 UI 线程未处理异常，便于定位 XAML/绑定/布局错误。</summary>
    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AvaStudio",
                "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] UI thread:{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不应改变异常处理流程。
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _mainViewModel = new MainViewModel();
        RequestedThemeVariant = _mainViewModel.AppThemeVariant;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };

            // 命令行/资源管理器传入媒体文件时，等窗口打开（视频输出已挂接）后再进入独立播放页。
            AttachExternalMediaOpen(desktop);

            // 后续进程通过命名管道转发文件/激活请求；先订阅再取走启动阶段积压的消息。
            SingleInstanceService.ArgumentsReceived += OnSingleInstanceArguments;
            foreach (var arguments in SingleInstanceService.DrainPending())
            {
                OnSingleInstanceArguments(arguments);
            }
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView
            {
                DataContext = _mainViewModel
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = _mainViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>后续实例的激活/文件打开请求切回 UI 线程处理。</summary>
    private void OnSingleInstanceArguments(string[] arguments)
    {
        Dispatcher.UIThread.Post(() => _ = HandleSingleInstanceAsync(arguments));
    }

    private async Task HandleSingleInstanceAsync(string[] arguments)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        ShowMainWindow();

        var path = FindExternalMediaPath(arguments);
        if (path is not null)
        {
            await OpenExternalMediaAsync(path).ConfigureAwait(true);
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// 启动参数里带受支持的媒体文件时登记一次“窗口已打开”回调：
    /// 主窗口的播放控件/视频输出在 Opened 之后才可用，因此不在构造阶段直接播放。
    /// 应用内点击媒体不经过这里，仍保持普通页面（迷你播放条）状态。
    /// </summary>
    private void AttachExternalMediaOpen(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_mainViewModel is null || desktop.MainWindow is not { } window)
        {
            return;
        }

        _pendingExternalMediaPath = FindExternalMediaPath(desktop.Args);
        if (_pendingExternalMediaPath is null)
        {
            return;
        }

        window.Opened += OnMainWindowOpenedForExternalMedia;
    }

    /// <summary>窗口首次打开后打开外部媒体，只执行一次。</summary>
    private void OnMainWindowOpenedForExternalMedia(object? sender, EventArgs e)
    {
        if (sender is Window openedWindow)
        {
            openedWindow.Opened -= OnMainWindowOpenedForExternalMedia;
        }

        var path = _pendingExternalMediaPath;
        _pendingExternalMediaPath = null;
        if (path is null)
        {
            return;
        }

        _ = OpenExternalMediaAsync(path);
    }

    /// <summary>取第一个存在且扩展名受支持的媒体路径，其余参数忽略。</summary>
    private static string? FindExternalMediaPath(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            var candidate = argument.Trim().Trim('"');
            if (!MediaFileTypes.IsSupported(candidate))
            {
                continue;
            }

            try
            {
                candidate = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>打开外部媒体并进入独立播放页；失败时回退为状态栏提示。</summary>
    private async Task OpenExternalMediaAsync(string path)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        try
        {
            await _mainViewModel.OpenExternalMediaAsync(path, enterImmersive: true);
        }
        catch (Exception exception)
        {
            _mainViewModel.ShowStatus($"无法打开媒体：{exception.Message}");
        }
    }
}
