using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaStudio.Playback;
using AvaStudio.Platform;
using AvaStudio.Services.Library;
using AvaStudio.Services.MediaTransport;
using AvaStudio.Services.Playback;
using AvaStudio.Services.Playlists;
using AvaStudio.Services.Settings;
using AvaStudio.ViewModels.Services;

namespace AvaStudio.Services;

/// <summary>
/// 应用组合根：集中创建并持有跨页面共享的服务。
/// 页面 ViewModel 只接收自己需要的服务，避免在视图层重新构造基础设施。
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private bool _disposed;

    public AppServices(DesktopService desktop, Func<IVideoOutput?> videoOutputAccessor)
    {
        Desktop = desktop;
        Settings = new AppSettingsService();
        ArtworkStore = new ArtworkStore(Settings.SettingsDirectory);
        ArtworkLoader = new ArtworkLoader();
        Library = new LibraryService(Settings, ArtworkStore, VideoThumbnailProviderFactory.Create());
        Playlists = new PlaylistStore(Settings);
        Playback = new PlaybackCoordinator(Library, Settings, videoOutputAccessor);
        MediaTransport = MediaTransportServiceFactory.Create();
        MediaTransport.PlayRequested += OnMediaTransportPlayRequested;
        MediaTransport.PauseRequested += OnMediaTransportPauseRequested;
        MediaTransport.NextRequested += OnMediaTransportNextRequested;
        MediaTransport.PreviousRequested += OnMediaTransportPreviousRequested;
        MediaTransport.StopRequested += OnMediaTransportStopRequested;
        MediaTransport.RaiseRequested += OnMediaTransportRaiseRequested;
        MediaTransport.QuitRequested += OnMediaTransportQuitRequested;
        MediaTransport.SeekRequested += OnMediaTransportSeekRequested;
        MediaTransport.VolumeRequested += OnMediaTransportVolumeRequested;
        MediaTransport.ShuffleRequested += OnMediaTransportShuffleRequested;
        MediaTransport.LoopStatusRequested += OnMediaTransportLoopStatusRequested;

        // 播放协调器是唯一事实来源：每次当前条目/状态变化都投影到系统媒体控制。
        Playback.CurrentItemChanged += OnPlaybackCurrentItemChanged;
        Playback.StateChanged += OnPlaybackStateChanged;
    }

    /// <summary>平台文件选择与系统外壳操作。</summary>
    public DesktopService Desktop { get; }

    /// <summary>应用设置。</summary>
    public AppSettingsService Settings { get; }

    /// <summary>封面提取与缓存。</summary>
    public ArtworkStore ArtworkStore { get; }

    /// <summary>界面封面解码器。</summary>
    public ArtworkLoader ArtworkLoader { get; }

    /// <summary>资料库扫描与聚合。</summary>
    public LibraryService Library { get; }

    /// <summary>播放列表持久化。</summary>
    public PlaylistStore Playlists { get; }

    /// <summary>播放队列与媒体会话协调。</summary>
    public PlaybackCoordinator Playback { get; }

    /// <summary>系统媒体控制集成（Windows 为 SMTC，Linux 为 MPRIS）。</summary>
    public IMediaTransportService MediaTransport { get; }

    /// <summary>启动时读取缓存；有资料库位置时在后台刷新索引。</summary>
    public async Task InitializeAsync()
    {
        // 系统媒体控制在 Avalonia 初始化完成后（UI 线程）注册，
        // 这样 SMTC 会话与窗口生命周期一致；失败只降级，不影响应用启动。
        await InitializeMediaTransportAsync().ConfigureAwait(true);

        await Library.InitializeAsync().ConfigureAwait(true);
        if (Settings.MusicFolders.Count > 0 || Settings.VideoFolders.Count > 0)
        {
            _ = Library.RefreshAsync();
        }
    }

    /// <summary>释放播放会话与原生上下文。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Playback.CurrentItemChanged -= OnPlaybackCurrentItemChanged;
        Playback.StateChanged -= OnPlaybackStateChanged;

        MediaTransport.PlayRequested -= OnMediaTransportPlayRequested;
        MediaTransport.PauseRequested -= OnMediaTransportPauseRequested;
        MediaTransport.NextRequested -= OnMediaTransportNextRequested;
        MediaTransport.PreviousRequested -= OnMediaTransportPreviousRequested;
        MediaTransport.StopRequested -= OnMediaTransportStopRequested;
        MediaTransport.RaiseRequested -= OnMediaTransportRaiseRequested;
        MediaTransport.QuitRequested -= OnMediaTransportQuitRequested;
        MediaTransport.SeekRequested -= OnMediaTransportSeekRequested;
        MediaTransport.VolumeRequested -= OnMediaTransportVolumeRequested;
        MediaTransport.ShuffleRequested -= OnMediaTransportShuffleRequested;
        MediaTransport.LoopStatusRequested -= OnMediaTransportLoopStatusRequested;

        // 先注销系统媒体会话，再关闭播放引擎，避免系统按钮在拆除过程中进入已释放的会话。
        try
        {
            MediaTransport.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MediaTransport] 释放系统媒体控制失败：{exception}");
        }

        await Playback.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>初始化系统媒体控制；任何异常都只记录并禁用本次会话的集成。</summary>
    private async Task InitializeMediaTransportAsync()
    {
        try
        {
            await MediaTransport.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MediaTransport] 初始化系统媒体控制失败：{exception}");
        }

        // 初始化完成前可能已经开始播放，补一次当前状态，保证浮出层信息完整。
        UpdateMediaTransportTrack();
        SyncMediaTransportState();
    }

    /// <summary>当前条目变化：刷新标题/封面，并同步一次状态。</summary>
    private void OnPlaybackCurrentItemChanged(object? sender, EventArgs e)
    {
        UpdateMediaTransportTrack();
        SyncMediaTransportState();
    }

    /// <summary>播放快照变化：同步阶段、进度与随机/循环模式。</summary>
    private void OnPlaybackStateChanged(object? sender, EventArgs e) => SyncMediaTransportState();

    private void UpdateMediaTransportTrack() => _ = UpdateMediaTransportTrackAsync();

    private async Task UpdateMediaTransportTrackAsync()
    {
        try
        {
            await MediaTransport.UpdateTrackAsync(Playback.CurrentItem).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MediaTransport] 更新当前媒体失败：{exception}");
        }
    }

    private void SyncMediaTransportState()
    {
        try
        {
            var snapshot = Playback.Snapshot;
            var duration = snapshot.Duration ?? Playback.CurrentItem?.Duration ?? TimeSpan.Zero;
            MediaTransport.UpdatePlaybackState(snapshot.Phase);
            MediaTransport.UpdatePosition(snapshot.Position, duration);
            MediaTransport.UpdateVolume(Playback.Volume / 100.0);
            MediaTransport.UpdatePlaybackMode(Playback.IsShuffle, Playback.RepeatMode);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MediaTransport] 同步播放状态失败：{exception}");
        }
    }

    /// <summary>
    /// SMTC 回调可能来自 WinRT 线程，所有播放操作都切回 UI 线程执行。
    /// </summary>
    private static void RunOnUiThread(Func<Task> operation)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await operation().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[MediaTransport] 执行系统媒体命令失败：{exception}");
            }
        });
    }

    /// <summary>系统“播放”：只在暂停/停止/结束状态下恢复，避免与界面按钮重复切换。</summary>
    private void OnMediaTransportPlayRequested(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            var phase = Playback.Snapshot.Phase;
            if (Playback.CurrentItem is null)
            {
                return Task.CompletedTask;
            }

            return phase is PlaybackPhase.Paused
                or PlaybackPhase.Stopped
                or PlaybackPhase.Ended
                or PlaybackPhase.Idle
                ? Playback.TogglePlayPauseAsync()
                : Task.CompletedTask;
        });
    }

    /// <summary>系统“暂停”：只在真正播放时暂停。</summary>
    private void OnMediaTransportPauseRequested(object? sender, EventArgs e)
    {
        RunOnUiThread(() => Playback.Snapshot.Phase == PlaybackPhase.Playing
            ? Playback.TogglePlayPauseAsync()
            : Task.CompletedTask);
    }

    private void OnMediaTransportNextRequested(object? sender, EventArgs e) =>
        RunOnUiThread(() => Playback.NextAsync());

    private void OnMediaTransportPreviousRequested(object? sender, EventArgs e) =>
        RunOnUiThread(() => Playback.PreviousAsync());

    /// <summary>系统“停止”回调；D-Bus/SMTC 线程只负责转发。</summary>
    private void OnMediaTransportStopRequested(object? sender, EventArgs e) =>
        RunOnUiThread(() => Playback.StopAsync());

    /// <summary>系统“Raise”回调（MPRIS）：把主窗口带到前台。</summary>
    private void OnMediaTransportRaiseRequested(object? sender, EventArgs e) =>
        RunOnUiThread(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is { } window)
            {
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

            return Task.CompletedTask;
        });

    /// <summary>系统“退出”回调（MPRIS Quit）：关闭主窗口并走正常释放流程。</summary>
    private void OnMediaTransportQuitRequested(object? sender, EventArgs e) =>
        RunOnUiThread(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow is { } window)
                {
                    window.Close();
                }
                else
                {
                    desktop.Shutdown();
                }
            }

            return Task.CompletedTask;
        });

    /// <summary>系统“跳转”回调；位置由 MPRIS/SMTC 换算成时间戳。</summary>
    private void OnMediaTransportSeekRequested(object? sender, TimeSpan position) =>
        RunOnUiThread(() => Playback.SeekAsync(position));

    /// <summary>系统“设置音量”回调；0..1 转换为应用音量，并在非零时解除静音。</summary>
    private void OnMediaTransportVolumeRequested(object? sender, double volume) =>
        RunOnUiThread(async () =>
        {
            if (volume > 0 && Playback.IsMuted)
            {
                await Playback.ToggleMuteAsync().ConfigureAwait(true);
            }

            await Playback.SetVolumeAsync(Math.Clamp(volume, 0.0, 1.0) * 100.0).ConfigureAwait(true);
        });

    /// <summary>系统“设置随机播放”回调。</summary>
    private void OnMediaTransportShuffleRequested(object? sender, bool shuffle) =>
        RunOnUiThread(() =>
        {
            Playback.SetShuffle(shuffle);
            return Task.CompletedTask;
        });

    /// <summary>系统“设置循环模式”回调。</summary>
    private void OnMediaTransportLoopStatusRequested(object? sender, RepeatMode repeatMode) =>
        RunOnUiThread(() =>
        {
            Playback.SetRepeatMode(repeatMode);
            return Task.CompletedTask;
        });
}
