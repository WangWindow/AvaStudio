using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Playback.Mpv;
using AvaStudio.Platform;
using AvaStudio.Services.Library;
using AvaStudio.Services.Settings;

namespace AvaStudio.Services.Playback;

/// <summary>循环模式。</summary>
public enum RepeatMode
{
    /// <summary>不循环，播放到队尾停止。</summary>
    None,

    /// <summary>循环整个队列。</summary>
    All,

    /// <summary>单曲循环。</summary>
    One,
}

/// <summary>
/// 播放协调层：维护运行时的播放队列，把队列操作映射到单文件 <see cref="IPlaybackSession"/>，
/// 并把引擎快照转换成界面可消费的状态。媒体核心仍然只认识单个文件；
/// 队列、随机、循环和自动下一首都在这里完成。
/// </summary>
public sealed class PlaybackCoordinator : IAsyncDisposable
{
    /// <summary>上一首时回到开头的判定阈值。</summary>
    private static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);

    /// <summary>等待原生视频表面创建的默认超时。</summary>
    private static readonly TimeSpan VideoSurfaceWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly LibraryService _library;
    private readonly AppSettingsService _settings;
    private readonly Func<IVideoOutput?> _videoOutputAccessor;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Random _random = new();
    private readonly DispatcherTimer _volumeSaveTimer;

    private MpvSession? _session;
    private MpvRuntime? _runtime;
    private bool _disposed;
    private bool _autoAdvancePending;
    private MediaItem? _currentItem;
    private int _currentIndex = -1;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Initial;
    private bool _shuffle;
    private RepeatMode _repeatMode = RepeatMode.None;
    private double _volume = AppSettingsService.DefaultVolume;
    private bool _isMuted;

    public PlaybackCoordinator(
        LibraryService library,
        AppSettingsService settings,
        Func<IVideoOutput?> videoOutputAccessor)
    {
        _library = library;
        _settings = settings;
        _videoOutputAccessor = videoOutputAccessor;
        _volume = settings.Volume;
        _isMuted = settings.IsMuted;

        // 拖动音量滑块时只更新引擎；停止拖动 500ms 后再写设置文件，避免频繁写盘。
        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _volumeSaveTimer.Tick += OnVolumeSaveTimerTick;
    }

    /// <summary>当前运行时队列。</summary>
    public ObservableCollection<MediaItem> Queue { get; } = [];

    /// <summary>当前播放条目；没有媒体时为 null。</summary>
    public MediaItem? CurrentItem => _currentItem;

    /// <summary>当前条目在队列中的索引；不在队列中时为 -1。</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>当前引擎快照。</summary>
    public PlaybackSnapshot Snapshot => _snapshot;

    /// <summary>是否随机播放。</summary>
    public bool IsShuffle => _shuffle;

    /// <summary>循环模式。</summary>
    public RepeatMode RepeatMode => _repeatMode;

    /// <summary>当前音量 0..100。</summary>
    public double Volume => _volume;

    /// <summary>是否静音。</summary>
    public bool IsMuted => _isMuted;

    /// <summary>当前媒体是否为视频。</summary>
    public bool IsVideo => _currentItem?.Kind == MediaItemKind.Video;

    /// <summary>播放器状态或队列发生变化。</summary>
    public event EventHandler? StateChanged;

    /// <summary>当前媒体发生变化（包括切歌与停止）。</summary>
    public event EventHandler? CurrentItemChanged;

    /// <summary>需要向用户展示的状态消息。</summary>
    public event Action<string>? StatusReported;

    /// <summary>
    /// 播放一个媒体条目。提供 <paramref name="context"/> 时用它替换运行时队列；
    /// 不提供时保留当前队列，仅在条目不存在时追加。
    /// </summary>
    public async Task PlayAsync(
        MediaItem item,
        IReadOnlyList<MediaItem>? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThrowIfDisposed();

        if (context is not null)
        {
            ReplaceQueue(context, item);
        }
        else
        {
            var index = IndexOf(item);
            if (index < 0)
            {
                Queue.Add(item);
                index = Queue.Count - 1;
            }

            SetCurrentIndex(index);
        }

        await PlayCurrentAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>播放队列中当前条目；用于播放/暂停按钮与自动续播。</summary>
    public async Task PlayCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_currentItem is null)
        {
            return;
        }

        try
        {
            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(true);
            _library.MarkPlayed(_currentItem);
            await session.OpenAsync(new LocalMediaSource(_currentItem.Path), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportStatus($"无法播放“{_currentItem.DisplayTitle}”：{exception.Message}");
        }
    }

    /// <summary>切换播放/暂停。没有当前条目时尝试播放队列第一项。</summary>
    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        if (_currentItem is null)
        {
            if (Queue.Count > 0)
            {
                SetCurrentIndex(0);
                await PlayCurrentAsync(cancellationToken).ConfigureAwait(true);
            }

            return;
        }

        try
        {
            // 队列首次加入条目时当前项已存在但引擎尚未加载；此时按播放应打开当前文件。
            if (_snapshot.Source is null || _snapshot.Phase == PlaybackPhase.Idle)
            {
                await PlayCurrentAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(true);
            switch (_snapshot.Phase)
            {
                case PlaybackPhase.Playing:
                    await session.PauseAsync(cancellationToken).ConfigureAwait(true);
                    break;
                case PlaybackPhase.Paused:
                case PlaybackPhase.Stopped:
                case PlaybackPhase.Ended:
                case PlaybackPhase.Failed:
                case PlaybackPhase.Idle:
                    await session.PlayAsync(cancellationToken).ConfigureAwait(true);
                    break;
                case PlaybackPhase.Opening:
                case PlaybackPhase.Closing:
                default:
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportStatus($"播放控制失败：{exception.Message}");
        }
    }

    /// <summary>播放下一首；自动续播时遵循循环模式。</summary>
    public async Task NextAsync(bool automatic = false, CancellationToken cancellationToken = default)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        if (automatic && _repeatMode == RepeatMode.One)
        {
            await PlayCurrentAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        var nextIndex = GetNextIndex(automatic);
        if (nextIndex < 0)
        {
            return;
        }

        SetCurrentIndex(nextIndex);
        await PlayCurrentAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>播放上一首；播放超过阈值时先回到开头。</summary>
    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        if (_snapshot.Position > PreviousRestartThreshold)
        {
            await SeekAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(true);
            return;
        }

        var previousIndex = _currentIndex <= 0 ? Queue.Count - 1 : _currentIndex - 1;
        SetCurrentIndex(previousIndex);
        await PlayCurrentAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>跳转到指定位置；未创建播放会话时忽略。</summary>
    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.SeekAsync(position, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportStatus($"跳转失败：{exception.Message}");
        }
    }

    /// <summary>设置音量并持久化。</summary>
    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
        if (_session is not null)
        {
            try
            {
                await _session.SetVolumeAsync(_volume, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportStatus($"音量设置失败：{exception.Message}");
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换静音并持久化。</summary>
    public async Task ToggleMuteAsync(CancellationToken cancellationToken = default)
    {
        _isMuted = !_isMuted;
        _settings.SetMuted(_isMuted);
        if (_session is not null)
        {
            try
            {
                await _session.SetMutedAsync(_isMuted, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportStatus($"静音设置失败：{exception.Message}");
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换随机播放。</summary>
    public void ToggleShuffle()
    {
        _shuffle = !_shuffle;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置随机播放（系统媒体控制使用）。</summary>
    public void SetShuffle(bool shuffle)
    {
        if (_shuffle == shuffle)
        {
            return;
        }

        _shuffle = shuffle;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按“不循环 → 全部循环 → 单曲循环”的顺序切换。</summary>
    public void CycleRepeatMode()
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None,
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置循环模式（系统媒体控制使用）。</summary>
    public void SetRepeatMode(RepeatMode repeatMode)
    {
        if (_repeatMode == repeatMode)
        {
            return;
        }

        _repeatMode = repeatMode;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>向队列追加条目，跳过已存在的路径。</summary>
    public int AddToQueue(IEnumerable<MediaItem> items)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (IndexOf(item) >= 0)
            {
                continue;
            }

            Queue.Add(item);
            added++;
        }

        if (_currentIndex < 0 && Queue.Count > 0)
        {
            SetCurrentIndex(0, notifyItemChanged: false);
        }

        if (added > 0)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>从队列移除条目；移除当前项时顺延到下一项。</summary>
    public bool RemoveFromQueue(MediaItem item)
    {
        var index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        var wasCurrent = index == _currentIndex;
        Queue.RemoveAt(index);
        if (wasCurrent)
        {
            if (Queue.Count == 0)
            {
                SetCurrentIndex(-1);
                _ = StopAsync();
            }
            else
            {
                SetCurrentIndex(Math.Min(index, Queue.Count - 1));
                _ = PlayCurrentAsync();
            }
        }
        else if (index < _currentIndex)
        {
            _currentIndex--;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>清空队列并停止当前播放。</summary>
    public async Task ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        Queue.Clear();
        SetCurrentIndex(-1);
        if (_session is not null)
        {
            try
            {
                await _session.StopAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportStatus($"停止播放失败：{exception.Message}");
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>停止当前播放但保留队列与当前条目，允许重新播放。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.StopAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportStatus($"停止播放失败：{exception.Message}");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按路径查找队列索引。</summary>
    public int IndexOf(MediaItem item)
    {
        for (var index = 0; index < Queue.Count; index++)
        {
            if (string.Equals(Queue[index].Path, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>关闭播放会话并释放原生上下文。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _volumeSaveTimer.Stop();
        _settings.SetVolume(_volume);
        if (_session is not null)
        {
            _session.SnapshotChanged -= OnSnapshotChanged;
            await _session.CloseAsync().ConfigureAwait(false);
            _session = null;
        }

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _runtime = null;
        }

        _sessionGate.Dispose();
    }

    /// <summary>确保播放会话已创建；libmpv 缺失或初始化失败时抛出带中文说明的异常。</summary>
    private async Task<MpvSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return _session;
        }

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_session is not null)
            {
                return _session;
            }

            var output = _videoOutputAccessor();
            if (output is { IsAvailable: false } && OperatingSystem.IsWindows())
            {
                // 原生视频表面可能刚加入可视树，短暂等待其 HWND/XID 创建。
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnHandleCreated(object? sender, EventArgs e) => completion.TrySetResult();
                output.HandleCreated += OnHandleCreated;
                try
                {
                    await Task.WhenAny(completion.Task, Task.Delay(VideoSurfaceWaitTimeout, cancellationToken))
                        .ConfigureAwait(true);
                }
                finally
                {
                    output.HandleCreated -= OnHandleCreated;
                }
            }

            _runtime = await Task.Run(() => MpvRuntime.Create(output), cancellationToken).ConfigureAwait(true);
            var session = new MpvSession(
                _runtime,
                action => Dispatcher.UIThread.Post(action));
            session.SnapshotChanged += OnSnapshotChanged;
            _session = session;
            await session.SetVolumeAsync(_volume, cancellationToken).ConfigureAwait(true);
            await session.SetMutedAsync(_isMuted, cancellationToken).ConfigureAwait(true);
            return session;
        }
        catch (Exception exception)
        {
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
                _runtime = null;
            }

            _session = null;
            throw new InvalidOperationException(
                $"无法加载 libmpv 播放引擎：{exception.Message}。请确认 libmpv 原生库已放在应用目录下。",
                exception);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>把外部提供的集合替换为运行时队列，并定位当前条目。</summary>
    private void ReplaceQueue(IReadOnlyList<MediaItem> context, MediaItem current)
    {
        Queue.Clear();
        foreach (var item in context)
        {
            if (IndexOf(item) < 0)
            {
                Queue.Add(item);
            }
        }

        var index = IndexOf(current);
        if (index < 0)
        {
            Queue.Add(current);
            index = Queue.Count - 1;
        }

        SetCurrentIndex(index);
    }

    /// <summary>更新当前索引并通知界面。</summary>
    private void SetCurrentIndex(int index, bool notifyItemChanged = true)
    {
        _currentIndex = index;
        var item = index >= 0 && index < Queue.Count ? Queue[index] : null;
        var changed = !ReferenceEquals(_currentItem, item);
        _currentItem = item;
        if (changed && notifyItemChanged)
        {
            CurrentItemChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>计算下一首索引；自动播放到队尾且不循环时返回 -1。</summary>
    private int GetNextIndex(bool automatic)
    {
        if (Queue.Count == 0)
        {
            return -1;
        }

        if (_shuffle)
        {
            if (Queue.Count == 1)
            {
                return automatic && _repeatMode == RepeatMode.None ? -1 : 0;
            }

            var candidate = _currentIndex;
            while (candidate == _currentIndex)
            {
                candidate = _random.Next(Queue.Count);
            }

            return candidate;
        }

        if (_currentIndex < Queue.Count - 1)
        {
            return _currentIndex + 1;
        }

        if (_repeatMode == RepeatMode.All || !automatic)
        {
            return 0;
        }

        return -1;
    }

    /// <summary>处理引擎快照；该回调已经被调度到 UI 线程。</summary>
    private void OnSnapshotChanged(object? sender, PlaybackSnapshotChangedEventArgs e)
    {
        _snapshot = e.Snapshot;
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (_snapshot.Phase == PlaybackPhase.Failed && _snapshot.Failure is { } failure)
        {
            ReportStatus($"播放失败：{failure.Message}");
        }
        else if (_snapshot.Phase == PlaybackPhase.Ended && !_autoAdvancePending)
        {
            _autoAdvancePending = true;
            _ = AdvanceAfterEndedAsync();
        }
    }

    /// <summary>播放结束后按循环/随机规则自动切歌。</summary>
    private async Task AdvanceAfterEndedAsync()
    {
        try
        {
            await Task.Yield();

            // 关闭“自动播放下一项”后停在当前条目；单曲循环仍是显式循环模式，继续工作。
            if (!_settings.AutoAdvance && _repeatMode != RepeatMode.One)
            {
                return;
            }

            await NextAsync(automatic: true).ConfigureAwait(true);
        }
        finally
        {
            _autoAdvancePending = false;
        }
    }

    /// <summary>音量滑块停止变化后写盘。</summary>
    private void OnVolumeSaveTimerTick(object? sender, EventArgs e)
    {
        _volumeSaveTimer.Stop();
        _settings.SetVolume(_volume);
    }

    /// <summary>向界面报告可展示的状态。</summary>
    private void ReportStatus(string message)
    {
        StatusReported?.Invoke(message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
