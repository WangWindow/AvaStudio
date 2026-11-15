#if WINDOWS_SMTC
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Services.Playback;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AvaStudio.Services.MediaTransport;

/// <summary>
/// 通过 Windows SMTC（系统媒体传输控件）把播放状态投影到快速设置媒体浮出、
/// 锁屏与硬件媒体键，并把系统按钮转成应用内的播放请求。
/// </summary>
/// <remarks>
/// 这里刻意复用 libmpv 的播放会话，只创建一个空的 <see cref="MediaPlayer"/>
/// 用来拿到 SMTC 会话，不接管任何媒体源；播放状态、进度都由应用推送。
/// SMTC 回调可能来自 WinRT 线程，因此只触发事件，不直接操作播放或界面。
/// </remarks>
public sealed class SmtcMediaTransportService : IMediaTransportService
{
    /// <summary>非强制刷新时间轴的最小间隔，避免每个快照都跨线程调用 WinRT。</summary>
    private static readonly TimeSpan TimelineUpdateInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _displayGate = new(1, 1);

    private MediaPlayer? _mediaPlayer;
    private SystemMediaTransportControls? _controls;
    private MediaItem? _currentItem;
    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _lastTimelinePosition;
    private DateTimeOffset _lastTimelineUpdate = DateTimeOffset.MinValue;
    private MediaPlaybackStatus? _appliedStatus;
    private MediaPlaybackAutoRepeatMode? _appliedRepeatMode;
    private bool? _appliedShuffle;
    private bool _shuffle;
    private RepeatMode _repeatMode = RepeatMode.None;
    private bool _initialized;
    private bool _disposed;
    private int _trackVersion;

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            if (_initialized || _disposed)
            {
                return Task.CompletedTask;
            }
        }

        try
        {
            var player = new MediaPlayer();

            // 媒体键由我们自己的 SMTC 会话处理；MediaPlayer 自带的命令管理器会抢同一次按键。
            player.CommandManager.IsEnabled = false;

            var controls = player.SystemMediaTransportControls;
            controls.IsEnabled = true;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsStopEnabled = true;
            controls.IsNextEnabled = true;
            controls.IsPreviousEnabled = true;
            controls.ButtonPressed += OnButtonPressed;
            controls.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;

            lock (_gate)
            {
                _mediaPlayer = player;
                _controls = controls;
                _initialized = true;
            }

            ApplyPlaybackStatus();
            ApplyPlaybackMode();
            UpdateTimelineProperties(force: true);

            Debug.WriteLine("[SMTC] 系统媒体控制已注册。");
        }
        catch (Exception exception)
        {
            // 初始化失败只意味着没有系统媒体集成，不应影响应用启动与播放。
            Debug.WriteLine($"[SMTC] 初始化失败：{exception}");
            DisposePlayer();
        }

        return Task.CompletedTask;
    }

    public async Task UpdateTrackAsync(MediaItem? item, CancellationToken cancellationToken = default)
    {
        int version;
        lock (_gate)
        {
            version = ++_trackVersion;
            var changed = !ReferenceEquals(_currentItem, item);
            _currentItem = item;
            if (changed)
            {
                _position = TimeSpan.Zero;
                _lastTimelinePosition = TimeSpan.Zero;
                _lastTimelineUpdate = DateTimeOffset.MinValue;
            }

            _duration = item?.Duration ?? TimeSpan.Zero;
        }

        if (GetControls() is null)
        {
            return;
        }

        await _displayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 快速切歌时旧任务可能排队到这里；当前版本已变化则不要再写回旧标题/封面。
            if (version != Volatile.Read(ref _trackVersion))
            {
                return;
            }

            await UpdateDisplayAsync(item, cancellationToken).ConfigureAwait(false);
            if (version != Volatile.Read(ref _trackVersion))
            {
                return;
            }

            ApplyPlaybackStatus();
            UpdateTimelineProperties(force: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SMTC] 更新媒体信息失败：{exception}");
        }
        finally
        {
            _displayGate.Release();
        }
    }

    public void UpdatePlaybackState(PlaybackPhase phase)
    {
        bool changed;
        lock (_gate)
        {
            changed = _phase != phase;
            _phase = phase;
        }

        if (!changed)
        {
            return;
        }

        ApplyPlaybackStatus();
        UpdateTimelineProperties(force: true);
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        lock (_gate)
        {
            _position = position;
            if (duration > TimeSpan.Zero)
            {
                _duration = duration;
            }
        }

        UpdateTimelineProperties(force: false);
    }

    public void UpdatePlaybackMode(bool shuffle, RepeatMode repeat)
    {
        bool changed;
        lock (_gate)
        {
            changed = _shuffle != shuffle || _repeatMode != repeat;
            _shuffle = shuffle;
            _repeatMode = repeat;
        }

        if (changed)
        {
            ApplyPlaybackMode();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        DisposePlayer();
    }

    private SystemMediaTransportControls? GetControls() => _controls;

    private async Task UpdateDisplayAsync(MediaItem? item, CancellationToken cancellationToken)
    {
        var controls = GetControls();
        if (controls is null)
        {
            return;
        }

        var updater = controls.DisplayUpdater;
        updater.ClearAll();

        if (item is null)
        {
            updater.Type = MediaPlaybackType.Music;
            updater.Update();
            return;
        }

        var isVideo = item.Kind == MediaItemKind.Video;

        // 音频没有自己缓存的封面时，交给系统从文件标签补齐标题/艺术家/封面；
        // 标签读取可能因文件被占用或格式异常失败，失败后仍用资料库元数据。
        if (!isVideo && string.IsNullOrWhiteSpace(item.ArtworkPath) && File.Exists(item.Path))
        {
            try
            {
                var mediaFile = await StorageFile.GetFileFromPathAsync(item.Path);
                await updater.CopyFromFileAsync(MediaPlaybackType.Music, mediaFile);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[SMTC] 读取文件元数据失败：{exception.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (isVideo)
        {
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = item.DisplayTitle;

            // 系统媒体卡片只显示文件夹名，不显示完整路径。
            var folder = Path.GetFileName(Path.GetDirectoryName(item.Path));
            updater.VideoProperties.Subtitle = string.IsNullOrWhiteSpace(folder)
                ? "视频"
                : folder;
        }
        else
        {
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = item.DisplayTitle;
            updater.MusicProperties.Artist = item.DisplayArtist;
            updater.MusicProperties.AlbumArtist = item.DisplayAlbumArtist;
            updater.MusicProperties.AlbumTitle = item.DisplayAlbum;
        }

        await ApplyThumbnailAsync(updater, item.ArtworkPath).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        updater.Update();
    }

    /// <summary>把资料库缓存的封面设为 SMTC 缩略图；封面缺失或不可读时保持无图。</summary>
    private static async Task ApplyThumbnailAsync(
        SystemMediaTransportControlsDisplayUpdater updater,
        string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath) || !File.Exists(artworkPath))
        {
            return;
        }

        try
        {
            var artworkFile = await StorageFile.GetFileFromPathAsync(artworkPath);
            updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(artworkFile);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SMTC] 设置缩略图失败：{exception.Message}");
        }
    }

    private void ApplyPlaybackStatus()
    {
        var controls = GetControls();
        if (controls is null)
        {
            return;
        }

        MediaPlaybackStatus status;
        lock (_gate)
        {
            status = _currentItem is null ? MediaPlaybackStatus.Stopped : MapPhase(_phase);
            if (_appliedStatus == status)
            {
                return;
            }

            _appliedStatus = status;
        }

        try
        {
            controls.PlaybackStatus = status;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _appliedStatus = null;
            }

            Debug.WriteLine($"[SMTC] 更新播放状态失败：{exception.Message}");
        }
    }

    private void ApplyPlaybackMode()
    {
        var controls = GetControls();
        if (controls is null)
        {
            return;
        }

        MediaPlaybackAutoRepeatMode repeatMode;
        bool shuffle;
        lock (_gate)
        {
            repeatMode = _repeatMode switch
            {
                RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
                RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None,
            };
            shuffle = _shuffle;

            if (_appliedRepeatMode == repeatMode && _appliedShuffle == shuffle)
            {
                return;
            }

            _appliedRepeatMode = repeatMode;
            _appliedShuffle = shuffle;
        }

        try
        {
            controls.AutoRepeatMode = repeatMode;
            controls.ShuffleEnabled = shuffle;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _appliedRepeatMode = null;
                _appliedShuffle = null;
            }

            Debug.WriteLine($"[SMTC] 更新播放模式失败：{exception.Message}");
        }
    }

    private void UpdateTimelineProperties(bool force)
    {
        var controls = GetControls();
        if (controls is null)
        {
            return;
        }

        MediaItem? currentItem;
        TimeSpan position;
        TimeSpan duration;
        lock (_gate)
        {
            currentItem = _currentItem;
            position = _position;
            duration = _duration;
        }

        if (currentItem is null)
        {
            if (!force)
            {
                return;
            }

            PublishTimeline(controls, TimeSpan.Zero, TimeSpan.Zero);
            return;
        }

        if (duration <= TimeSpan.Zero)
        {
            duration = currentItem.Duration;
        }

        position = ClampPosition(position, duration);

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!force
                && now - _lastTimelineUpdate < TimelineUpdateInterval
                && Math.Abs((position - _lastTimelinePosition).TotalSeconds) < TimelineUpdateInterval.TotalSeconds)
            {
                return;
            }

            _lastTimelineUpdate = now;
            _lastTimelinePosition = position;
        }

        PublishTimeline(controls, position, duration);
    }

    private static void PublishTimeline(
        SystemMediaTransportControls controls,
        TimeSpan position,
        TimeSpan duration)
    {
        try
        {
            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = position,
                MaxSeekTime = duration,
                EndTime = duration,
            });
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SMTC] 更新时间轴失败：{exception.Message}");
        }
    }

    /// <summary>把播放阶段映射为系统显示状态；结束/失败等终态一律显示为停止。</summary>
    private static MediaPlaybackStatus MapPhase(PlaybackPhase phase) => phase switch
    {
        PlaybackPhase.Playing => MediaPlaybackStatus.Playing,
        PlaybackPhase.Opening => MediaPlaybackStatus.Paused,
        PlaybackPhase.Paused => MediaPlaybackStatus.Paused,
        _ => MediaPlaybackStatus.Stopped,
    };

    private void OnButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        try
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    PlayRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    PauseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PreviousRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    StopRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception exception)
        {
            // 事件订阅方的异常不能抛回 WinRT 回调线程。
            Debug.WriteLine($"[SMTC] 处理媒体按钮失败：{exception}");
        }
    }

    private void OnPlaybackPositionChangeRequested(
        SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args)
    {
        try
        {
            var position = ClampPosition(args.RequestedPlaybackPosition, GetDuration());
            lock (_gate)
            {
                _position = position;
            }

            // 跳转后立即刷新，避免浮出层在下一个快照前显示旧进度。
            UpdateTimelineProperties(force: true);
            SeekRequested?.Invoke(this, position);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[SMTC] 处理跳转请求失败：{exception}");
        }
    }

    private TimeSpan GetDuration()
    {
        lock (_gate)
        {
            return _duration;
        }
    }

    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration <= TimeSpan.Zero || position <= duration)
        {
            return position;
        }

        return duration;
    }

    private void DisposePlayer()
    {
        var controls = _controls;
        _controls = null;

        if (controls is not null)
        {
            try
            {
                controls.ButtonPressed -= OnButtonPressed;
                controls.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
                controls.IsEnabled = false;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[SMTC] 注销媒体会话失败：{exception.Message}");
            }
        }

        var player = _mediaPlayer;
        _mediaPlayer = null;
        if (player is not null)
        {
            try
            {
                player.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[SMTC] 释放 MediaPlayer 失败：{exception.Message}");
            }
        }

        lock (_gate)
        {
            _initialized = false;
            _appliedStatus = null;
            _appliedRepeatMode = null;
            _appliedShuffle = null;
        }
    }
}
#else
#pragma warning disable CS0067
using System;
using System.Threading;
using System.Threading.Tasks;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Services.Playback;

namespace AvaStudio.Services.MediaTransport;

/// <summary>非 Windows 构建占位实现；真正逻辑在 WINDOWS_SMTC 分支。</summary>
public sealed class SmtcMediaTransportService : IMediaTransportService
{
    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateTrackAsync(MediaItem? item, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void UpdatePlaybackState(PlaybackPhase phase)
    {
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
    }

    public void UpdatePlaybackMode(bool shuffle, RepeatMode repeat)
    {
    }

    public void Dispose()
    {
    }
}
#pragma warning restore CS0067
#endif
