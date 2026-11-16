using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// Single-threaded libmpv session. One background thread owns <c>mpv_wait_event</c>; all
/// engine commands are submitted asynchronously with correlated reply ids and committed
/// through one serial queue. Snapshot state is only written here and published through the
/// injected dispatcher, never by the event loop directly.
/// </summary>
public sealed class MpvSession : IPlaybackSession
{
    private static readonly bool LogEnabled = string.Equals(
        Environment.GetEnvironmentVariable("AVASTUDIO_MPV_LOG"),
        "1",
        StringComparison.Ordinal);

    private static readonly (string Name, int Format)[] ObservedProperties =
    [
        ("time-pos", LibMpv.FormatDouble),
        ("duration", LibMpv.FormatDouble),
        ("pause", LibMpv.FormatFlag),
        ("core-idle", LibMpv.FormatFlag),
        ("eof-reached", LibMpv.FormatFlag),
        ("seeking", LibMpv.FormatFlag),
        ("paused-for-cache", LibMpv.FormatFlag),
        ("cache-buffering-state", LibMpv.FormatInt64),
        ("volume", LibMpv.FormatDouble),
        ("mute", LibMpv.FormatFlag),
        ("video-format", LibMpv.FormatString),
        ("audio-format", LibMpv.FormatString),
        ("media-title", LibMpv.FormatString),
        ("idle-active", LibMpv.FormatFlag),
    ];

    private readonly Lock _sync = new();
    private readonly MpvRuntime _runtime;
    private readonly IntPtr _context;
    private readonly Action<Action> _snapshotDispatcher;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Dictionary<ulong, TaskCompletionSource<int>> _pending = [];
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _eventLoop;

    private PlaybackSnapshot _snapshot;
    private PlaybackSnapshot _publishedSnapshot;
    private long _lastPublishTimestamp;
    private long _generation;
    private long _revision;
    private ulong _requestId;
    private LocalMediaSource? _source;
    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private PlaybackIntent _intent = PlaybackIntent.Stop;
    private TimeSpan _position;
    private TimeSpan? _duration;
    private double _volume = 100;
    private bool _muted;
    private MediaKind _mediaKind = MediaKind.Unknown;
    private bool _isSeeking;
    private bool _pausedForCache;
    private double? _buffering;
    private PlaybackFailure? _failure;
    private PlaybackLifecycle _lifecycle = PlaybackLifecycle.Running;
    private bool _paused;
    private bool _loadInFlight;
    private bool _sawStartFile;
    private long _loadGeneration;
    private ulong _inFlightLoadId;
    private string? _videoFormat;
    private string? _audioFormat;
    private string? _mediaTitle;
    private bool _closeStarted;
    private volatile bool _closing;

    public MpvSession(MpvRuntime runtime, Action<Action>? snapshotDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _context = runtime.Context;
        _snapshotDispatcher = snapshotDispatcher ?? DefaultDispatch;
        _snapshot = PlaybackSnapshot.Initial;
        _publishedSnapshot = _snapshot;
        _lastPublishTimestamp = Stopwatch.GetTimestamp();

        ObserveProperties();

        _eventLoop = new Thread(RunEventLoop)
        {
            IsBackground = true,
            Name = "libmpv-event-loop",
        };
        _eventLoop.Start();
    }

    public event EventHandler<PlaybackSnapshotChangedEventArgs>? SnapshotChanged;

    public PlaybackSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _publishedSnapshot;
            }
        }
    }

    /// <summary>
    /// Title reported by libmpv for the loaded file. Kept out of the frozen snapshot contract
    /// until the system-media-controls stage consumes it.
    /// </summary>
    public string? MediaTitle
    {
        get
        {
            lock (_sync)
            {
                return _mediaTitle;
            }
        }
    }

    public Task OpenAsync(LocalMediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return LoadAsync(source, cancellationToken);
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        PlaybackPhase phase;
        LocalMediaSource? source;
        lock (_sync)
        {
            phase = _phase;
            source = _source;
        }

        switch (phase)
        {
            case PlaybackPhase.Playing:
                return;
            case PlaybackPhase.Paused:
                await SetPauseAsync(false, cancellationToken).ConfigureAwait(false);
                return;
            case PlaybackPhase.Stopped:
            case PlaybackPhase.Ended:
            case PlaybackPhase.Failed:
            case PlaybackPhase.Idle:
                if (source is not null)
                {
                    await LoadAsync(source, cancellationToken).ConfigureAwait(false);
                }

                return;
            default:
                return;
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        lock (_sync)
        {
            if (_phase != PlaybackPhase.Playing)
            {
                return;
            }
        }

        await SetPauseAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            bool shouldStop = false;
            Mutate(() =>
            {
                if (_phase is not (PlaybackPhase.Opening or PlaybackPhase.Playing or PlaybackPhase.Paused))
                {
                    return;
                }

                shouldStop = true;
                if (_inFlightLoadId != 0)
                {
                    AbortRequestLocked(_inFlightLoadId);
                }

                _inFlightLoadId = 0;
                _loadGeneration = -1;
                _loadInFlight = false;
                _phase = PlaybackPhase.Stopped;
                _intent = PlaybackIntent.Stop;
                _paused = false;
                _isSeeking = false;
                _pausedForCache = false;
                _buffering = null;
            });

            if (shouldStop)
            {
                using MpvArgv argv = MpvArgv.Create("stop");
                _ = LibMpv.mpv_command_async(_context, 0, argv.Pointer);
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        PlaybackPhase phase;
        TimeSpan? duration;
        LocalMediaSource? source;
        lock (_sync)
        {
            phase = _phase;
            duration = _duration;
            source = _source;
        }

        if (phase is PlaybackPhase.Idle
            or PlaybackPhase.Opening
            or PlaybackPhase.Stopped
            or PlaybackPhase.Failed
            or PlaybackPhase.Closing)
        {
            return;
        }

        double seconds = Math.Max(0, position.TotalSeconds);
        if (duration is { Ticks: > 0 } knownDuration)
        {
            seconds = Math.Min(seconds, knownDuration.TotalSeconds);
        }

        string seekTarget = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        string[] seekArgs = ["seek", seekTarget, "absolute+exact"];

        if (phase == PlaybackPhase.Ended)
        {
            // libmpv rejects seeks while it is parked at EOF with keep-open. Reload the same
            // source (still the frozen replay path) and seek once the file is playing again.
            if (source is null)
            {
                return;
            }

            await LoadAsync(source, cancellationToken).ConfigureAwait(false);
            _ = await SendCommandAsync(seekArgs, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await SendCommandAsync(seekArgs, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        double clamped = Math.Clamp(volume, 0, 100);
        await SendPropertyAsync(
            "volume",
            LibMpv.FormatDouble,
            data => Marshal.StructureToPtr(clamped, data, false),
            sizeof(double),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        int value = isMuted ? 1 : 0;
        await SendPropertyAsync(
            "mute",
            LibMpv.FormatFlag,
            data => Marshal.WriteInt32(data, value),
            sizeof(int),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        bool startedHere;
        lock (_sync)
        {
            startedHere = !_closeStarted;
            if (startedHere)
            {
                _closeStarted = true;
            }
        }

        if (!startedHere)
        {
            await _closeCompletion.Task.ConfigureAwait(false);
            return;
        }

        await _commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Mutate(
                () =>
                {
                    _lifecycle = PlaybackLifecycle.Closing;
                    _phase = PlaybackPhase.Closing;
                    _intent = PlaybackIntent.Stop;
                    _loadInFlight = false;
                    _inFlightLoadId = 0;
                    _loadGeneration = -1;
                    CompletePendingLocked();
                },
                force: true);

            _closing = true;
            LibMpv.mpv_wakeup(_context);
        }
        finally
        {
            _commandGate.Release();
        }

        if (Thread.CurrentThread != _eventLoop)
        {
            _eventLoop.Join();
        }

        await _runtime.DisposeAsync().ConfigureAwait(false);

        Mutate(
            () =>
            {
                _lifecycle = PlaybackLifecycle.Closed;
                _isSeeking = false;
                _pausedForCache = false;
                _buffering = null;
            },
            force: true);

        _closeCompletion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private static void DefaultDispatch(Action publish)
    {
        _ = ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), publish);
    }

    private async Task LoadAsync(LocalMediaSource source, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long generation = 0;
        ulong requestId = 0;
        int submitError;

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            Mutate(() =>
            {
                generation = ++_generation;
                _source = source;
                _phase = PlaybackPhase.Opening;
                _intent = PlaybackIntent.Play;
                _position = TimeSpan.Zero;
                _duration = null;
                _mediaKind = MediaKind.Unknown;
                _videoFormat = null;
                _audioFormat = null;
                _mediaTitle = null;
                _failure = null;
                _paused = false;
                _isSeeking = false;
                _pausedForCache = false;
                _buffering = null;
                _sawStartFile = false;
                _loadInFlight = true;

                if (_inFlightLoadId != 0)
                {
                    AbortRequestLocked(_inFlightLoadId);
                }

                requestId = ++_requestId;
                _inFlightLoadId = requestId;
                _loadGeneration = generation;
                _pending.Add(requestId, completion);
            });

            // keep-open leaves pause set at end-of-file; clear it before the new file starts.
            _ = SetPropertyNoReply("pause", LibMpv.FormatFlag, static data => Marshal.WriteInt32(data, 0));

            using MpvArgv argv = MpvArgv.Create("loadfile", source.Path, "replace");
            submitError = LibMpv.mpv_command_async(_context, requestId, argv.Pointer);
        }
        finally
        {
            _commandGate.Release();
        }

        if (submitError < 0)
        {
            lock (_sync)
            {
                _pending.Remove(requestId, out _);
            }

            completion.TrySetResult(submitError);
        }

        int reply = await completion.Task.ConfigureAwait(false);
        if (reply < 0)
        {
            FailCurrentLoad(generation, reply);
        }
    }

    private void FailCurrentLoad(long generation, int error)
    {
        string message = LibMpv.ErrorString(error);
        Mutate(() =>
        {
            if (_lifecycle != PlaybackLifecycle.Running
                || _generation != generation
                || _loadGeneration != generation)
            {
                return;
            }

            _phase = PlaybackPhase.Failed;
            _intent = PlaybackIntent.Stop;
            _failure = new PlaybackFailure(PlaybackFailureKind.OpenFailed, message);
            _loadInFlight = false;
            _inFlightLoadId = 0;
            _sawStartFile = false;
            _isSeeking = false;
            _pausedForCache = false;
            _buffering = null;
        });
    }

    private Task<int> SetPauseAsync(bool paused, CancellationToken cancellationToken)
    {
        int value = paused ? 1 : 0;
        return SendPropertyAsync(
            "pause",
            LibMpv.FormatFlag,
            data => Marshal.WriteInt32(data, value),
            sizeof(int),
            cancellationToken);
    }

    private Task<int> SendCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        return SubmitAsync(
            requestId =>
            {
                using MpvArgv argv = MpvArgv.Create(args);
                return LibMpv.mpv_command_async(_context, requestId, argv.Pointer);
            },
            cancellationToken);
    }

    private Task<int> SendPropertyAsync(
        string name,
        int format,
        Action<IntPtr> write,
        int size,
        CancellationToken cancellationToken)
    {
        return SubmitAsync(
            requestId =>
            {
                IntPtr data = Marshal.AllocHGlobal(size);
                try
                {
                    write(data);
                    return LibMpv.mpv_set_property_async(_context, requestId, name, format, data);
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            },
            cancellationToken);
    }

    private int SetPropertyNoReply(string name, int format, Action<IntPtr> write)
    {
        IntPtr data = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            write(data);
            return LibMpv.mpv_set_property_async(_context, 0, name, format, data);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private async Task<int> SubmitAsync(Func<ulong, int> submit, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong requestId;
        int error;

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            lock (_sync)
            {
                requestId = ++_requestId;
                _pending.Add(requestId, completion);
            }

            error = submit(requestId);
            if (error < 0)
            {
                lock (_sync)
                {
                    _pending.Remove(requestId);
                }

                completion.TrySetResult(error);
            }
        }
        finally
        {
            _commandGate.Release();
        }

        if (error < 0)
        {
            return error;
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() => AbortRequest(requestId));
        return await completion.Task.ConfigureAwait(false);
    }

    private void AbortRequest(ulong requestId)
    {
        TaskCompletionSource<int>? completion;
        bool running;
        lock (_sync)
        {
            running = _lifecycle == PlaybackLifecycle.Running;
            _pending.Remove(requestId, out completion);
        }

        if (!running || completion is null)
        {
            completion?.TrySetResult(LibMpv.ErrorCommand);
            return;
        }

        LibMpv.mpv_abort_async_command(_context, requestId);
        completion.TrySetResult(LibMpv.ErrorCommand);
    }

    private void AbortRequestLocked(ulong requestId)
    {
        if (_pending.Remove(requestId, out TaskCompletionSource<int>? completion))
        {
            completion.TrySetResult(LibMpv.ErrorCommand);
        }

        LibMpv.mpv_abort_async_command(_context, requestId);
    }

    private void CompletePendingLocked()
    {
        foreach (TaskCompletionSource<int> completion in _pending.Values)
        {
            completion.TrySetResult(LibMpv.ErrorCommand);
        }

        _pending.Clear();
    }

    private void ThrowIfClosed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_lifecycle != PlaybackLifecycle.Running, this);
        }
    }

    private void Mutate(Action mutation, bool force = false)
    {
        PlaybackSnapshot published;
        lock (_sync)
        {
            mutation();
            _snapshot = CreateSnapshotLocked();
            if (!force && !ShouldPublishLocked(_snapshot))
            {
                return;
            }

            _publishedSnapshot = _snapshot;
            _lastPublishTimestamp = Stopwatch.GetTimestamp();
            published = _snapshot;
        }

        try
        {
            _snapshotDispatcher(() => SnapshotChanged?.Invoke(this, new PlaybackSnapshotChangedEventArgs(published)));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Playback snapshot dispatch failed: {exception}");
        }
    }

    private bool ShouldPublishLocked(PlaybackSnapshot candidate)
    {
        if (candidate == _publishedSnapshot)
        {
            return false;
        }

        PlaybackSnapshot withoutPosition = candidate with { Position = _publishedSnapshot.Position };
        if (withoutPosition == _publishedSnapshot)
        {
            long now = Stopwatch.GetTimestamp();
            return now - _lastPublishTimestamp >= Stopwatch.Frequency / 10;
        }

        return true;
    }

    private PlaybackSnapshot CreateSnapshotLocked()
    {
        PlaybackCapabilities capabilities = ComputeCapabilitiesLocked();
        PlaybackSnapshot candidate = new(
            _generation,
            _revision + 1,
            _source,
            _phase,
            _intent,
            _position,
            _duration,
            capabilities,
            _volume,
            _muted,
            _mediaKind,
            _isSeeking || _pausedForCache || _loadInFlight,
            _pausedForCache ? _buffering : null,
            _failure,
            _lifecycle);

        if (candidate with { Revision = _revision } == _snapshot)
        {
            return _snapshot;
        }

        _revision++;
        return candidate;
    }

    private PlaybackCapabilities ComputeCapabilitiesLocked()
    {
        bool hasSource = _source is not null;
        return new PlaybackCapabilities(
            CanPlay: hasSource && _phase is PlaybackPhase.Idle
                or PlaybackPhase.Paused
                or PlaybackPhase.Stopped
                or PlaybackPhase.Ended
                or PlaybackPhase.Failed,
            CanPause: _phase is PlaybackPhase.Playing or PlaybackPhase.Paused,
            CanSeek: hasSource && _duration is { Ticks: > 0 } && _phase is PlaybackPhase.Playing
                or PlaybackPhase.Paused
                or PlaybackPhase.Ended,
            CanStop: _phase is PlaybackPhase.Opening or PlaybackPhase.Playing or PlaybackPhase.Paused);
    }

    private void ObserveProperties()
    {
        foreach ((string name, int format) in ObservedProperties)
        {
            int error = LibMpv.mpv_observe_property(_context, 0, name, format);
            if (error < 0)
            {
                Debug.WriteLine($"mpv_observe_property({name}) failed: {LibMpv.ErrorString(error)}");
            }
        }
    }

    private void RunEventLoop()
    {
        try
        {
            while (true)
            {
                IntPtr eventPointer = LibMpv.mpv_wait_event(_context, -1);
                if (_closing)
                {
                    return;
                }

                if (eventPointer == IntPtr.Zero)
                {
                    continue;
                }

                MpvEvent mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
                if (mpvEvent.EventId == (int)MpvEventId.Shutdown)
                {
                    return;
                }

                if (mpvEvent.EventId == (int)MpvEventId.None)
                {
                    continue;
                }

                HandleEvent(mpvEvent);
            }
        }
        catch (Exception exception)
        {
            if (!_closing)
            {
                Debug.WriteLine($"libmpv event loop failed: {exception}");
            }
        }
    }

    private void HandleEvent(MpvEvent mpvEvent)
    {
        switch ((MpvEventId)mpvEvent.EventId)
        {
            case MpvEventId.PropertyChange:
                HandlePropertyChange(mpvEvent);
                break;
            case MpvEventId.CommandReply:
            case MpvEventId.SetPropertyReply:
                HandleCommandReply(mpvEvent);
                break;
            case MpvEventId.StartFile:
                HandleStartFile();
                break;
            case MpvEventId.FileLoaded:
                HandleFileLoaded();
                break;
            case MpvEventId.EndFile:
                HandleEndFile(mpvEvent);
                break;
            case MpvEventId.LogMessage:
                HandleLogMessage(mpvEvent);
                break;
            default:
                break;
        }
    }

    private void HandleCommandReply(MpvEvent mpvEvent)
    {
        TaskCompletionSource<int>? completion;
        lock (_sync)
        {
            _pending.Remove(mpvEvent.ReplyUserdata, out completion);
        }

        completion?.TrySetResult(mpvEvent.Error);
    }

    private void HandleStartFile()
    {
        lock (_sync)
        {
            if (_loadInFlight)
            {
                _sawStartFile = true;
            }
        }
    }

    private void HandleFileLoaded()
    {
        MediaKind trackKind = ReadTrackMediaKind();
        TimeSpan? duration = TryGetDoubleProperty("duration") is double seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
        string? mediaTitle = GetStringProperty("media-title");
        string? videoFormat = GetStringProperty("video-format");
        string? audioFormat = GetStringProperty("audio-format");

        Mutate(() =>
        {
            if (!_loadInFlight || _loadGeneration != _generation)
            {
                return;
            }

            _loadInFlight = false;
            _inFlightLoadId = 0;
            _sawStartFile = true;
            _failure = null;
            _phase = _paused ? PlaybackPhase.Paused : PlaybackPhase.Playing;
            _intent = _paused ? PlaybackIntent.Pause : PlaybackIntent.Play;
            _isSeeking = false;
            _pausedForCache = false;
            _buffering = null;

            if (duration is not null)
            {
                _duration = duration;
            }

            if (mediaTitle is not null)
            {
                _mediaTitle = mediaTitle;
            }

            if (videoFormat is not null)
            {
                _videoFormat = videoFormat;
            }

            if (audioFormat is not null)
            {
                _audioFormat = audioFormat;
            }

            if (trackKind != MediaKind.Unknown)
            {
                _mediaKind = trackKind;
            }
            else
            {
                UpdateMediaKindLocked();
            }
        });
    }

    private MediaKind ReadTrackMediaKind()
    {
        MediaKind kind = MediaKind.Unknown;
        long trackCount = GetInt64Property("track-list/count");
        for (long index = 0; index < trackCount; index++)
        {
            string trackIndex = index.ToString(CultureInfo.InvariantCulture);
            string? type = GetStringProperty("track-list/" + trackIndex + "/type");
            if (string.Equals(type, "video", StringComparison.Ordinal))
            {
                return MediaKind.Video;
            }

            if (string.Equals(type, "audio", StringComparison.Ordinal))
            {
                kind = MediaKind.Audio;
            }
        }

        return kind;
    }

    private string? GetStringProperty(string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            int error = LibMpv.mpv_get_property(_context, name, LibMpv.FormatString, buffer);
            if (error < 0)
            {
                return null;
            }

            IntPtr value = Marshal.ReadIntPtr(buffer);
            if (value == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(value);
            }
            finally
            {
                LibMpv.mpv_free(value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private double? TryGetDoubleProperty(string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            int error = LibMpv.mpv_get_property(_context, name, LibMpv.FormatDouble, buffer);
            return error < 0 ? null : Marshal.PtrToStructure<double>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private long GetInt64Property(string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            int error = LibMpv.mpv_get_property(_context, name, LibMpv.FormatInt64, buffer);
            return error < 0 ? 0 : Marshal.ReadInt64(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void HandleEndFile(MpvEvent mpvEvent)
    {
        if (mpvEvent.Data == IntPtr.Zero)
        {
            return;
        }

        MpvEventEndFile endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
        switch ((MpvEndFileReason)endFile.Reason)
        {
            case MpvEndFileReason.Eof:
                if (IsStaleEndFile())
                {
                    return;
                }

                Mutate(() =>
                {
                    if (_phase is not (PlaybackPhase.Opening or PlaybackPhase.Playing or PlaybackPhase.Paused))
                    {
                        return;
                    }

                    _phase = PlaybackPhase.Ended;
                    _intent = PlaybackIntent.Stop;
                    _paused = true;
                    _loadInFlight = false;
                    _inFlightLoadId = 0;
                    _isSeeking = false;
                    _pausedForCache = false;
                    _buffering = null;
                });
                break;
            case MpvEndFileReason.Stop:
                if (IsStaleEndFile())
                {
                    return;
                }

                Mutate(() =>
                {
                    if (_phase is not (PlaybackPhase.Opening or PlaybackPhase.Playing or PlaybackPhase.Paused))
                    {
                        return;
                    }

                    _phase = PlaybackPhase.Stopped;
                    _intent = PlaybackIntent.Stop;
                    _loadInFlight = false;
                    _inFlightLoadId = 0;
                    _isSeeking = false;
                    _pausedForCache = false;
                    _buffering = null;
                });
                break;
            case MpvEndFileReason.Error:
                if (IsStaleEndFile())
                {
                    return;
                }

                string message = endFile.Error != 0 ? LibMpv.ErrorString(endFile.Error) : "播放失败。";
                Mutate(() =>
                {
                    if (_phase is not (PlaybackPhase.Opening or PlaybackPhase.Playing or PlaybackPhase.Paused))
                    {
                        return;
                    }

                    _phase = PlaybackPhase.Failed;
                    _intent = PlaybackIntent.Stop;
                    _failure = new PlaybackFailure(PlaybackFailureKind.PlaybackFailed, message);
                    _loadInFlight = false;
                    _inFlightLoadId = 0;
                    _isSeeking = false;
                    _pausedForCache = false;
                    _buffering = null;
                });
                break;
            case MpvEndFileReason.Redirect:
            case MpvEndFileReason.Quit:
            default:
                break;
        }
    }

    private bool IsStaleEndFile()
    {
        lock (_sync)
        {
            return _loadInFlight && !_sawStartFile;
        }
    }

    private void HandleLogMessage(MpvEvent mpvEvent)
    {
        if (!LogEnabled || mpvEvent.Data == IntPtr.Zero)
        {
            return;
        }

        MpvEventLogMessage message = Marshal.PtrToStructure<MpvEventLogMessage>(mpvEvent.Data);
        string prefix = Marshal.PtrToStringUTF8(message.Prefix) ?? string.Empty;
        string level = Marshal.PtrToStringUTF8(message.Level) ?? string.Empty;
        string text = Marshal.PtrToStringUTF8(message.Text) ?? string.Empty;
        Debug.WriteLine($"[mpv/{prefix}] {level}: {text}");
    }

    private void HandlePropertyChange(MpvEvent mpvEvent)
    {
        if (mpvEvent.Data == IntPtr.Zero)
        {
            return;
        }

        MpvEventProperty property = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
        if (property.Name == IntPtr.Zero)
        {
            return;
        }

        string name = Marshal.PtrToStringUTF8(property.Name) ?? string.Empty;
        bool loadInFlight;
        lock (_sync)
        {
            loadInFlight = _loadInFlight;
        }

        // Metadata/time-pos events from the previous file must not bleed into the new
        // generation while its loadfile is still in flight. FILE_LOADED re-seeds these
        // values from the engine.
        if (loadInFlight
            && name is "time-pos" or "duration" or "video-format" or "audio-format" or "media-title" or "pause")
        {
            return;
        }

        switch (name)
        {
            case "time-pos":
                if (TryReadDouble(property, out double seconds) && seconds >= 0)
                {
                    TimeSpan position = TimeSpan.FromSeconds(seconds);
                    Mutate(() => _position = position);
                }

                break;
            case "duration":
                if (TryReadDouble(property, out double duration) && duration > 0)
                {
                    TimeSpan value = TimeSpan.FromSeconds(duration);
                    Mutate(() => _duration = value);
                }
                else
                {
                    Mutate(() => _duration = null);
                }

                break;
            case "pause":
                if (TryReadFlag(property, out bool paused))
                {
                    Mutate(() =>
                    {
                        _paused = paused;

                        // keep-open reports pause=true at EOF; Ended owns that state.
                        if (_phase is PlaybackPhase.Playing or PlaybackPhase.Paused)
                        {
                            _phase = paused ? PlaybackPhase.Paused : PlaybackPhase.Playing;
                            _intent = paused ? PlaybackIntent.Pause : PlaybackIntent.Play;
                        }
                    });
                }

                break;
            case "core-idle":
                // core-idle is also true while a paused file sits at EOF and while no file is
                // loaded. Opening is owned exclusively by Open/reload, never by this property.
                break;
            case "eof-reached":
                if (TryReadFlag(property, out bool eofReached) && eofReached)
                {
                    Mutate(() =>
                    {
                        if (_phase is PlaybackPhase.Playing or PlaybackPhase.Paused)
                        {
                            _phase = PlaybackPhase.Ended;
                            _intent = PlaybackIntent.Stop;
                            _paused = true;
                            _loadInFlight = false;
                            _inFlightLoadId = 0;
                            _isSeeking = false;
                            _pausedForCache = false;
                            _buffering = null;
                        }
                    });
                }

                break;
            case "seeking":
                if (TryReadFlag(property, out bool seeking))
                {
                    // Position updates are throttled; force the final seek position out when
                    // the seek ends so a paused seek is visible immediately.
                    Mutate(() => _isSeeking = seeking, force: !seeking);
                }

                break;
            case "paused-for-cache":
                if (TryReadFlag(property, out bool pausedForCache))
                {
                    Mutate(() =>
                    {
                        _pausedForCache = pausedForCache;
                        if (!pausedForCache)
                        {
                            _buffering = null;
                        }
                    });
                }

                break;
            case "cache-buffering-state":
                if (TryReadInt64(property, out long buffering))
                {
                    Mutate(() =>
                    {
                        if (_pausedForCache)
                        {
                            _buffering = Math.Clamp(buffering / 100.0, 0, 1);
                        }
                    });
                }

                break;
            case "volume":
                if (TryReadDouble(property, out double volume))
                {
                    Mutate(() => _volume = Math.Clamp(volume, 0, 100));
                }

                break;
            case "mute":
                if (TryReadFlag(property, out bool muted))
                {
                    Mutate(() => _muted = muted);
                }

                break;
            case "video-format":
                Mutate(() =>
                {
                    _videoFormat = ReadString(property);
                    UpdateMediaKindLocked();
                });
                break;
            case "audio-format":
                Mutate(() =>
                {
                    _audioFormat = ReadString(property);
                    UpdateMediaKindLocked();
                });
                break;
            case "media-title":
                Mutate(() => _mediaTitle = ReadString(property));
                break;
            case "idle-active":
            default:
                break;
        }
    }

    private void UpdateMediaKindLocked()
    {
        bool hasVideo = !string.IsNullOrEmpty(_videoFormat)
            && !string.Equals(_videoFormat, "no", StringComparison.Ordinal);
        bool hasAudio = !string.IsNullOrEmpty(_audioFormat)
            && !string.Equals(_audioFormat, "no", StringComparison.Ordinal);

        _mediaKind = hasVideo
            ? MediaKind.Video
            : hasAudio
                ? MediaKind.Audio
                : MediaKind.Unknown;
    }

    private static string? ReadString(MpvEventProperty property)
    {
        if (property.Format != LibMpv.FormatString || property.Data == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.PtrToStringUTF8(property.Data);
    }

    private static bool TryReadDouble(MpvEventProperty property, out double value)
    {
        if (property.Format == LibMpv.FormatDouble && property.Data != IntPtr.Zero)
        {
            value = Marshal.PtrToStructure<double>(property.Data);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadFlag(MpvEventProperty property, out bool value)
    {
        if (property.Format == LibMpv.FormatFlag && property.Data != IntPtr.Zero)
        {
            value = Marshal.ReadInt32(property.Data) != 0;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadInt64(MpvEventProperty property, out long value)
    {
        if (property.Format == LibMpv.FormatInt64 && property.Data != IntPtr.Zero)
        {
            value = Marshal.ReadInt64(property.Data);
            return true;
        }

        value = 0;
        return false;
    }
}
