using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaStudio.Services.Library;
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

    /// <summary>托盘图标点击：播放/暂停。</summary>
    private void OnPlayPauseClick(object? sender, EventArgs e)
    {
        if (_mainViewModel?.PlayerBar.TogglePlayPauseCommand.CanExecute(null) == true)
        {
            _mainViewModel.PlayerBar.TogglePlayPauseCommand.Execute(null);
        }
    }

    /// <summary>托盘菜单：显示主窗口。</summary>
    private void OnShowWindowClick(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    /// <summary>托盘菜单：切换到轻量模式（隐藏主窗口，保留托盘与播放）。</summary>
    private void OnLightweightModeClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return;
        }

        if (window.IsVisible)
        {
            window.Hide();
            if (sender is NativeMenuItem item)
            {
                item.IsChecked = true;
            }
        }
        else
        {
            ShowMainWindow();
            if (sender is NativeMenuItem item)
            {
                item.IsChecked = false;
            }
        }
    }

    /// <summary>托盘菜单：上一首。</summary>
    private void OnPreviousTrackClick(object? sender, EventArgs e)
    {
        if (_mainViewModel?.PlayerBar.PreviousCommand.CanExecute(null) == true)
        {
            _mainViewModel.PlayerBar.PreviousCommand.Execute(null);
        }
    }

    /// <summary>托盘菜单：下一首。</summary>
    private void OnNextTrackClick(object? sender, EventArgs e)
    {
        if (_mainViewModel?.PlayerBar.NextCommand.CanExecute(null) == true)
        {
            _mainViewModel.PlayerBar.NextCommand.Execute(null);
        }
    }

    /// <summary>托盘菜单：释放播放会话并退出。</summary>
    private void OnExitClick(object? sender, EventArgs e)
    {
        if (_mainViewModel is not null)
        {
            _ = _mainViewModel.DisposeAsync();
            _mainViewModel = null;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return;
        }

        window.Show();
        window.WindowState = WindowState.Normal;
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
