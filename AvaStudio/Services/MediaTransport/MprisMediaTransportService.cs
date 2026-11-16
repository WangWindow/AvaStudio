#if LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Services.Playback;
using Tmds.DBus.Protocol;

namespace AvaStudio.Services.MediaTransport;

/// <summary>
/// 通过 MPRIS（D-Bus 媒体播放器接口）把播放状态投影到 Linux 桌面环境，
/// 让 GNOME 通知栏/快捷设置、媒体键与 <c>playerctl</c> 等客户端显示并控制播放。
/// </summary>
/// <remarks>
/// 桌面只负责展示与转发；播放状态仍以 <c>PlaybackCoordinator</c> 为准。
/// 所有 D-Bus 回调都在连接读取线程上执行，因此这里只更新受锁保护的快照并触发事件，
/// 真正的播放操作由 <c>AppServices</c> 切回 UI 线程执行。
/// </remarks>
public sealed class MprisMediaTransportService : IMediaTransportService, IPathMethodHandler
{
    private const string PlayerName = "org.mpris.MediaPlayer2.avastudio";
    private const string PlayerPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string NoTrackPath = "/org/mpris/MediaPlayer2/TrackList/NoTrack";
    private const string TrackPathPrefix = "/com/avastudio/track/";

    private static readonly TimeSpan PositionSignalInterval = TimeSpan.FromSeconds(1);

    private static readonly string[] RootPropertyNames =
    [
        "CanQuit",
        "Fullscreen",
        "CanSetFullscreen",
        "CanRaise",
        "HasTrackList",
        "Identity",
        "DesktopEntry",
        "SupportedUriSchemes",
        "SupportedMimeTypes",
    ];

    private static readonly string[] PlayerPropertyNames =
    [
        "PlaybackStatus",
        "LoopStatus",
        "Rate",
        "Shuffle",
        "Metadata",
        "Volume",
        "Position",
        "MinimumRate",
        "MaximumRate",
        "CanGoNext",
        "CanGoPrevious",
        "CanPlay",
        "CanPause",
        "CanSeek",
        "CanControl",
    ];

    /// <summary>对象管理接口声明；GUI 客户端据此发现可用的 MPRIS 播放器。</summary>
    private static ReadOnlyMemory<byte> RootInterfaceXml { get; } =
        """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="Fullscreen" type="b" access="read"/>
          <property name="CanSetFullscreen" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>

        """u8.ToArray();

    /// <summary>播放器接口声明；通知栏与媒体键使用这些状态和方法。</summary>
    private static ReadOnlyMemory<byte> PlayerInterfaceXml { get; } =
        """
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek">
            <arg direction="in" type="x" name="Offset"/>
          </method>
          <method name="SetPosition">
            <arg direction="in" type="o" name="TrackId"/>
            <arg direction="in" type="x" name="Position"/>
          </method>
          <method name="OpenUri">
            <arg direction="in" type="s" name="Uri"/>
          </method>
          <signal name="Seeked">
            <arg type="x" name="Position"/>
          </signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>

        """u8.ToArray();

    private readonly object _gate = new();
    private readonly object _signalGate = new();

    private DBusConnection? _connection;
    private MediaItem? _item;
    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _lastReportedPosition;
    private DateTimeOffset _lastPositionSignal = DateTimeOffset.MinValue;
    private double _volume = 1.0;
    private bool _shuffle;
    private RepeatMode _repeatMode = RepeatMode.None;
    private long _trackSerial;
    private bool _initialized;
    private bool _disposed;

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? RaiseRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<double>? VolumeRequested;
    public event EventHandler<bool>? ShuffleRequested;
    public event EventHandler<RepeatMode>? LoopStatusRequested;

    /// <inheritdoc />
    public string Path => PlayerPath;

    /// <inheritdoc />
    public bool HandlesChildPaths => false;

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

        return InitializeCoreAsync(cancellationToken);
    }

    /// <summary>连接会话总线并注册 MPRIS 名称；失败只记录，不阻断应用启动。</summary>
    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        string? address = DBusAddress.Session;
        if (string.IsNullOrWhiteSpace(address))
        {
            Debug.WriteLine("[MPRIS] 当前会话没有 D-Bus，跳过系统媒体集成。");
            return;
        }

        var connection = new DBusConnection(address);
        var disposeConnection = false;
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!await connection.TryRequestNameAsync(PlayerName, RequestNameOptions.Default).ConfigureAwait(false)
                && !await connection.TryRequestNameAsync(
                    $"{PlayerName}.instance{Environment.ProcessId}",
                    RequestNameOptions.Default).ConfigureAwait(false))
            {
                Debug.WriteLine("[MPRIS] 无法取得媒体播放器总线名称，跳过系统媒体集成。");
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    disposeConnection = true;
                }
            }

            if (disposeConnection)
            {
                return;
            }

            connection.AddMethodHandler(this);

            lock (_gate)
            {
                _connection = connection;
                _initialized = true;
            }

            Debug.WriteLine($"[MPRIS] 已注册 {PlayerName}。");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[MPRIS] 初始化已取消。");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MPRIS] 初始化失败：{exception}");
        }
        finally
        {
            if (!_initialized)
            {
                try
                {
                    connection.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[MPRIS] 释放连接失败：{exception.Message}");
                }
            }
        }
    }

    public Task UpdateTrackAsync(MediaItem? item, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            var trackChanged = !string.Equals(_item?.Path, item?.Path, StringComparison.Ordinal);
            _item = item;
            if (trackChanged)
            {
                _trackSerial++;
                _position = TimeSpan.Zero;
                _lastReportedPosition = TimeSpan.Zero;
                _lastPositionSignal = DateTimeOffset.MinValue;
                _duration = item?.Duration ?? TimeSpan.Zero;
            }
            else if (item is not null && item.Duration > TimeSpan.Zero)
            {
                _duration = item.Duration;
            }
        }

        EmitPropertiesChanged("Metadata", "CanSeek", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause");
        return Task.CompletedTask;
    }

    public void UpdatePlaybackState(PlaybackPhase phase)
    {
        lock (_gate)
        {
            if (_disposed || _phase == phase)
            {
                return;
            }

            _phase = phase;
        }

        EmitPropertiesChanged("PlaybackStatus", "Position");
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        bool seeked;
        bool shouldSignal;
        long positionMicroseconds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (duration > TimeSpan.Zero)
            {
                _duration = duration;
            }

            var clamped = ClampPosition(position, _duration);
            seeked = Math.Abs((clamped - _lastReportedPosition).TotalSeconds) >= 2;
            _position = clamped;
            _lastReportedPosition = clamped;
            positionMicroseconds = ToMicroseconds(clamped);

            var now = DateTimeOffset.UtcNow;
            shouldSignal = _phase == PlaybackPhase.Playing
                && now - _lastPositionSignal >= PositionSignalInterval;
            if (shouldSignal || seeked)
            {
                _lastPositionSignal = now;
            }
        }

        if (seeked)
        {
            EmitSeeked(positionMicroseconds);
            EmitPropertiesChanged("Position");
        }
        else if (shouldSignal)
        {
            EmitPropertiesChanged("Position");
        }
    }

    public void UpdatePlaybackMode(bool shuffle, RepeatMode repeat)
    {
        bool shuffleChanged;
        bool repeatChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            shuffleChanged = _shuffle != shuffle;
            repeatChanged = _repeatMode != repeat;
            _shuffle = shuffle;
            _repeatMode = repeat;
        }

        if (!shuffleChanged && !repeatChanged)
        {
            return;
        }

        var changed = new List<string>(2);
        if (shuffleChanged)
        {
            changed.Add("Shuffle");
        }

        if (repeatChanged)
        {
            changed.Add("LoopStatus");
        }

        EmitPropertiesChanged([.. changed]);
    }

    public void UpdateVolume(double volume)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (_gate)
        {
            if (_disposed || Math.Abs(_volume - clamped) < 0.0001)
            {
                return;
            }

            _volume = clamped;
        }

        EmitPropertiesChanged("Volume");
    }

    public void Dispose()
    {
        DBusConnection? connection;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            connection = _connection;
            _connection = null;
        }

        if (connection is null)
        {
            return;
        }

        try
        {
            connection.RemoveMethodHandler(PlayerPath);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MPRIS] 注销方法处理器失败：{exception.Message}");
        }

        try
        {
            connection.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[MPRIS] 释放连接失败：{exception.Message}");
        }
    }

    /// <inheritdoc />
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([RootInterfaceXml, PlayerInterfaceXml]);
            return default;
        }

        var request = context.Request;
        var interfaceName = request.InterfaceAsString;
        var member = request.MemberAsString;
        var signature = request.SignatureAsString;

        if (interfaceName == PropertiesInterface)
        {
            switch ((member, signature))
            {
                case ("Get", "ss"):
                    HandleGetProperty(context);
                    return default;
                case ("GetAll", "s"):
                    HandleGetAllProperties(context);
                    return default;
                case ("Set", "ssv"):
                    HandleSetProperty(context);
                    return default;
            }
        }
        else if (interfaceName == RootInterface)
        {
            switch ((member, signature))
            {
                case ("Raise", ""):
                    ReplyEmpty(context);
                    RaiseRequested?.Invoke(this, EventArgs.Empty);
                    return default;
                case ("Quit", ""):
                    ReplyEmpty(context);
                    QuitRequested?.Invoke(this, EventArgs.Empty);
                    return default;
            }
        }
        else if (interfaceName == PlayerInterface)
        {
            switch ((member, signature))
            {
                case ("Next", ""):
                    ReplyEmpty(context);
                    NextRequested?.Invoke(this, EventArgs.Empty);
                    return default;
                case ("Previous", ""):
                    ReplyEmpty(context);
                    PreviousRequested?.Invoke(this, EventArgs.Empty);
                    return default;
                case ("Pause", ""):
                    ReplyEmpty(context);
                    PauseRequested?.Invoke(this, EventArgs.Empty);
                    return default;
                case ("PlayPause", ""):
                    ReplyEmpty(context);
                    PlayPauseRequested(context);
                    return default;
                case ("Stop", ""):
                    ReplyEmpty(context);
                    StopRequested?.Invoke(this, EventArgs.Empty);
                    return default;
                case ("Play", ""):
                    ReplyEmpty(context);
                    PlayRequested?.Invoke(this, EventArgs.Empty);
                    return default;
                case ("Seek", "x"):
                    HandleSeek(context);
                    return default;
                case ("SetPosition", "ox"):
                    HandleSetPosition(context);
                    return default;
                case ("OpenUri", "s"):
                    context.ReplyError(
                        "org.freedesktop.DBus.Error.NotSupported",
                        "AvaStudio 不接受 MPRIS OpenUri 请求。");
                    return default;
            }
        }

        context.ReplyUnknownMethodError();
        return default;
    }

    /// <summary>根据当前阶段把 MPRIS 的 PlayPause 转换成播放或暂停事件。</summary>
    private void PlayPauseRequested(MethodContext context)
    {
        PlaybackPhase phase;
        lock (_gate)
        {
            phase = _phase;
        }

        if (phase == PlaybackPhase.Playing)
        {
            PauseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            PlayRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>处理 org.freedesktop.DBus.Properties.Get，返回单个属性的变体值。</summary>
    private void HandleGetProperty(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var interfaceName = reader.ReadString();
        var propertyName = reader.ReadString();

        if (!TryGetProperty(interfaceName, propertyName, out var value))
        {
            context.ReplyError(
                "org.freedesktop.DBus.Error.InvalidArgs",
                $"未知 MPRIS 属性：{interfaceName}.{propertyName}");
            return;
        }

        using var writer = context.CreateReplyWriter("v");
        writer.WriteVariant(value);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>处理 org.freedesktop.DBus.Properties.GetAll，返回指定接口的全部属性。</summary>
    private void HandleGetAllProperties(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var interfaceName = reader.ReadString();
        if (!TryGetAllProperties(interfaceName, out var properties))
        {
            context.ReplyError(
                "org.freedesktop.DBus.Error.InvalidArgs",
                $"未知 MPRIS 接口：{interfaceName}");
            return;
        }

        using var writer = context.CreateReplyWriter("a{sv}");
        writer.WriteDictionary(properties);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>处理可写属性；音量、循环与随机模式会转回应用。</summary>
    private void HandleSetProperty(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var interfaceName = reader.ReadString();
        var propertyName = reader.ReadString();
        var value = reader.ReadVariantValue();

        if (interfaceName != PlayerInterface)
        {
            context.ReplyError(
                "org.freedesktop.DBus.Error.PropertyReadOnly",
                $"MPRIS 属性 {interfaceName}.{propertyName} 为只读。");
            return;
        }

        switch (propertyName)
        {
            case "Volume":
                if (value.Type != VariantValueType.Double)
                {
                    ReplyInvalidArgument(context, propertyName, "d");
                    return;
                }

                var volume = Math.Clamp(value.GetDouble(), 0.0, 1.0);
                lock (_gate)
                {
                    _volume = volume;
                }

                ReplyEmpty(context);
                EmitPropertiesChanged("Volume");
                VolumeRequested?.Invoke(this, volume);
                return;

            case "LoopStatus":
                if (value.Type != VariantValueType.String
                    || !TryParseLoopStatus(value.GetString(), out var repeatMode))
                {
                    ReplyInvalidArgument(context, propertyName, "s");
                    return;
                }

                lock (_gate)
                {
                    _repeatMode = repeatMode;
                }

                ReplyEmpty(context);
                EmitPropertiesChanged("LoopStatus");
                LoopStatusRequested?.Invoke(this, repeatMode);
                return;

            case "Shuffle":
                if (value.Type != VariantValueType.Bool)
                {
                    ReplyInvalidArgument(context, propertyName, "b");
                    return;
                }

                var shuffle = value.GetBool();
                lock (_gate)
                {
                    _shuffle = shuffle;
                }

                ReplyEmpty(context);
                EmitPropertiesChanged("Shuffle");
                ShuffleRequested?.Invoke(this, shuffle);
                return;

            case "Rate":
                if (value.Type != VariantValueType.Double)
                {
                    ReplyInvalidArgument(context, propertyName, "d");
                    return;
                }

                if (Math.Abs(value.GetDouble() - 1.0) > 0.0001)
                {
                    context.ReplyError(
                        "org.freedesktop.DBus.Error.NotSupported",
                        "AvaStudio 暂不支持改变播放速率。");
                    return;
                }

                ReplyEmpty(context);
                return;

            default:
                context.ReplyError(
                    "org.freedesktop.DBus.Error.PropertyReadOnly",
                    $"MPRIS 属性 {propertyName} 为只读。");
                return;
        }
    }

    /// <summary>处理相对跳转；偏移量单位为微秒。</summary>
    private void HandleSeek(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var offsetMicroseconds = reader.ReadInt64();

        TimeSpan duration;
        TimeSpan position;
        long positionMicroseconds;
        lock (_gate)
        {
            duration = _duration;
            position = ClampPosition(_position + TimeSpan.FromMicroseconds(offsetMicroseconds), duration);
            _position = position;
            _lastReportedPosition = position;
            positionMicroseconds = ToMicroseconds(position);
        }

        ReplyEmpty(context);
        EmitSeeked(positionMicroseconds);
        EmitPropertiesChanged("Position");
        SeekRequested?.Invoke(this, position);
    }

    /// <summary>处理绝对跳转；只有 TrackId 与当前媒体一致时才接受。</summary>
    private void HandleSetPosition(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var trackId = reader.ReadObjectPath();
        var requestedMicroseconds = reader.ReadInt64();

        TimeSpan duration;
        TimeSpan position;
        long positionMicroseconds;
        bool matchesTrack;
        lock (_gate)
        {
            matchesTrack = string.Equals(trackId.ToString(), GetTrackPathLocked(), StringComparison.Ordinal);
            duration = _duration;
            position = ClampPosition(TimeSpan.FromMicroseconds(requestedMicroseconds), duration);
            if (matchesTrack)
            {
                _position = position;
                _lastReportedPosition = position;
            }

            positionMicroseconds = ToMicroseconds(position);
        }

        if (!matchesTrack)
        {
            context.ReplyError(
                "org.freedesktop.DBus.Error.InvalidArgs",
                "SetPosition 的 TrackId 与当前媒体不一致。");
            return;
        }

        ReplyEmpty(context);
        EmitSeeked(positionMicroseconds);
        EmitPropertiesChanged("Position");
        SeekRequested?.Invoke(this, position);
    }

    /// <summary>读取单个 MPRIS 属性；未知接口或属性返回 false。</summary>
    private bool TryGetProperty(string interfaceName, string propertyName, out VariantValue value)
    {
        lock (_gate)
        {
            return interfaceName switch
            {
                RootInterface => TryGetRootPropertyLocked(propertyName, out value),
                PlayerInterface => TryGetPlayerPropertyLocked(propertyName, out value),
                _ => Fail(out value),
            };
        }
    }

    /// <summary>读取接口的全部属性；未知接口返回 false。</summary>
    private bool TryGetAllProperties(string interfaceName, out Dictionary<string, VariantValue> properties)
    {
        lock (_gate)
        {
            switch (interfaceName)
            {
                case RootInterface:
                    properties = new Dictionary<string, VariantValue>(RootPropertyNames.Length);
                    foreach (var name in RootPropertyNames)
                    {
                        if (TryGetRootPropertyLocked(name, out var value))
                        {
                            properties[name] = value;
                        }
                    }

                    return true;
                case PlayerInterface:
                    properties = new Dictionary<string, VariantValue>(PlayerPropertyNames.Length);
                    foreach (var name in PlayerPropertyNames)
                    {
                        if (TryGetPlayerPropertyLocked(name, out var value))
                        {
                            properties[name] = value;
                        }
                    }

                    return true;
                default:
                    properties = [];
                    return false;
            }
        }
    }

    /// <summary>根接口属性；可选能力全部按“不支持”上报。</summary>
    private bool TryGetRootPropertyLocked(string propertyName, out VariantValue value)
    {
        switch (propertyName)
        {
            case "Fullscreen":
            case "CanSetFullscreen":
            case "HasTrackList":
                value = false;
                return true;
            case "CanRaise":
            case "CanQuit":
                value = true;
                return true;
            case "Identity":
                value = "AvaStudio";
                return true;
            case "DesktopEntry":
                value = string.Empty;
                return true;
            case "SupportedUriSchemes":
            case "SupportedMimeTypes":
                value = VariantValue.Array(Array.Empty<string>());
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>播放器接口属性；调用方必须持有 <see cref="_gate"/>。</summary>
    private bool TryGetPlayerPropertyLocked(string propertyName, out VariantValue value)
    {
        switch (propertyName)
        {
            case "PlaybackStatus":
                value = MapPlaybackStatus(_phase);
                return true;
            case "LoopStatus":
                value = MapLoopStatus(_repeatMode);
                return true;
            case "Rate":
            case "MinimumRate":
            case "MaximumRate":
                value = 1.0;
                return true;
            case "Shuffle":
                value = _shuffle;
                return true;
            case "Metadata":
                value = new Dict<string, VariantValue>(BuildMetadataLocked());
                return true;
            case "Volume":
                value = Math.Clamp(_volume, 0.0, 1.0);
                return true;
            case "Position":
                value = ToMicroseconds(ClampPosition(_position, _duration));
                return true;
            case "CanGoNext":
            case "CanGoPrevious":
            case "CanPlay":
            case "CanPause":
                value = _item is not null;
                return true;
            case "CanSeek":
                value = _duration > TimeSpan.Zero;
                return true;
            case "CanControl":
                value = true;
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>按当前条目构建 MPRIS 元数据；没有媒体时返回空字典。</summary>
    private Dictionary<string, VariantValue> BuildMetadataLocked()
    {
        var metadata = new Dictionary<string, VariantValue>(10);
        if (_item is null)
        {
            return metadata;
        }

        metadata["mpris:trackid"] = new ObjectPath(GetTrackPathLocked());
        var length = _duration > TimeSpan.Zero ? _duration : _item.Duration;
        if (length > TimeSpan.Zero)
        {
            metadata["mpris:length"] = ToMicroseconds(length);
        }

        metadata["xesam:title"] = _item.DisplayTitle;
        metadata["xesam:artist"] = VariantValue.Array(new[] { _item.DisplayArtist });
        metadata["xesam:album"] = _item.DisplayAlbum;
        metadata["xesam:albumArtist"] = VariantValue.Array(new[] { _item.DisplayAlbumArtist });
        if (!string.IsNullOrWhiteSpace(_item.Genre))
        {
            metadata["xesam:genre"] = VariantValue.Array(new[] { _item.Genre });
        }

        if (_item.TrackNumber is { } trackNumber && trackNumber <= int.MaxValue)
        {
            metadata["xesam:trackNumber"] = (int)trackNumber;
        }

        if (!string.IsNullOrWhiteSpace(_item.ArtworkPath) && File.Exists(_item.ArtworkPath))
        {
            metadata["mpris:artUrl"] = ToFileUri(_item.ArtworkPath);
        }

        metadata["xesam:url"] = ToFileUri(_item.Path);
        return metadata;
    }

    /// <summary>当前媒体的稳定对象路径；切歌时递增序号。</summary>
    private string GetTrackPathLocked() =>
        _item is null ? NoTrackPath : $"{TrackPathPrefix}{_trackSerial}";

    /// <summary>发送 org.freedesktop.DBus.Properties.PropertiesChanged 信号。</summary>
    private void EmitPropertiesChanged(params string[] propertyNames)
    {
        DBusConnection? connection;
        Dictionary<string, VariantValue> changed;
        lock (_gate)
        {
            connection = _connection;
            if (connection is null || !_initialized)
            {
                return;
            }

            changed = new Dictionary<string, VariantValue>(propertyNames.Length);
            foreach (var name in propertyNames)
            {
                if (TryGetPlayerPropertyLocked(name, out var value))
                {
                    changed[name] = value;
                }
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        lock (_signalGate)
        {
            try
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(
                    path: PlayerPath,
                    @interface: PropertiesInterface,
                    member: "PropertiesChanged",
                    signature: "sa{sv}as");
                writer.WriteString(PlayerInterface);
                writer.WriteDictionary(changed);
                writer.WriteArray(Array.Empty<string>());
                connection.TrySendMessage(writer.CreateMessage());
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[MPRIS] 发送属性变更失败：{exception.Message}");
            }
        }
    }

    /// <summary>发送 Player.Seeked 信号，通知客户端位置发生了跳变。</summary>
    private void EmitSeeked(long positionMicroseconds)
    {
        DBusConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            if (connection is null || !_initialized)
            {
                return;
            }
        }

        lock (_signalGate)
        {
            try
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(
                    path: PlayerPath,
                    @interface: PlayerInterface,
                    member: "Seeked",
                    signature: "x");
                writer.WriteInt64(positionMicroseconds);
                connection.TrySendMessage(writer.CreateMessage());
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[MPRIS] 发送 Seeked 失败：{exception.Message}");
            }
        }
    }

    /// <summary>回复没有返回值的方法调用。</summary>
    private static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>回复属性参数类型错误。</summary>
    private static void ReplyInvalidArgument(MethodContext context, string propertyName, string expectedType)
    {
        context.ReplyError(
            "org.freedesktop.DBus.Error.InvalidArgs",
            $"MPRIS 属性 {propertyName} 需要 {expectedType} 类型的值。");
    }

    /// <summary>统一清空 out 参数并返回失败。</summary>
    private static bool Fail(out VariantValue value)
    {
        value = default;
        return false;
    }

    /// <summary>把内部播放阶段映射为 MPRIS PlaybackStatus。</summary>
    private static string MapPlaybackStatus(PlaybackPhase phase) => phase switch
    {
        PlaybackPhase.Playing => "Playing",
        PlaybackPhase.Opening => "Paused",
        PlaybackPhase.Paused => "Paused",
        _ => "Stopped",
    };

    /// <summary>把内部循环模式映射为 MPRIS LoopStatus。</summary>
    private static string MapLoopStatus(RepeatMode repeatMode) => repeatMode switch
    {
        RepeatMode.One => "Track",
        RepeatMode.All => "Playlist",
        _ => "None",
    };

    /// <summary>解析 MPRIS LoopStatus；未知值返回 false。</summary>
    private static bool TryParseLoopStatus(string value, out RepeatMode repeatMode)
    {
        switch (value)
        {
            case "None":
                repeatMode = RepeatMode.None;
                return true;
            case "Track":
                repeatMode = RepeatMode.One;
                return true;
            case "Playlist":
                repeatMode = RepeatMode.All;
                return true;
            default:
                repeatMode = RepeatMode.None;
                return false;
        }
    }

    /// <summary>把跳转位置限制在 0..时长范围内。</summary>
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

    /// <summary>把时间转换为 MPRIS 使用的微秒。</summary>
    private static long ToMicroseconds(TimeSpan value) =>
        Math.Max(0, (long)Math.Round(value.Ticks / (double)TimeSpan.TicksPerMicrosecond));

    /// <summary>生成 MPRIS artUrl/xesam:url 使用的本地文件 URI。</summary>
    private static string ToFileUri(string path) => new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
}
#endif
