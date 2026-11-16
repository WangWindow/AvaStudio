using System;
using System.Threading;
using System.Threading.Tasks;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Services.Playback;

namespace AvaStudio.Services.MediaTransport;

/// <summary>
/// 把播放状态投影到系统媒体控制，并把系统发起的播放指令转回应用。
/// </summary>
/// <remarks>
/// 平台适配器只负责展示与转发，播放状态仍以 <c>PlaybackCoordinator</c> 为准；
/// 不支持系统媒体控制的平台使用 <see cref="NoopMediaTransportService"/>。
/// </remarks>
public interface IMediaTransportService : IDisposable
{
    /// <summary>系统请求播放。</summary>
    event EventHandler? PlayRequested;

    /// <summary>系统请求暂停。</summary>
    event EventHandler? PauseRequested;

    /// <summary>系统请求下一首。</summary>
    event EventHandler? NextRequested;

    /// <summary>系统请求上一首。</summary>
    event EventHandler? PreviousRequested;

    /// <summary>系统请求停止。</summary>
    event EventHandler? StopRequested;

    /// <summary>系统请求把应用窗口带到前台。</summary>
    event EventHandler? RaiseRequested;

    /// <summary>系统请求退出应用。</summary>
    event EventHandler? QuitRequested;

    /// <summary>系统请求跳转到指定位置。</summary>
    event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>系统请求设置音量，参数范围为 0..1。</summary>
    event EventHandler<double>? VolumeRequested;

    /// <summary>系统请求设置随机播放。</summary>
    event EventHandler<bool>? ShuffleRequested;

    /// <summary>系统请求设置循环模式。</summary>
    event EventHandler<RepeatMode>? LoopStatusRequested;

    /// <summary>初始化平台媒体控制；失败时实现应自行降级，不抛异常打断启动。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>更新当前媒体信息；传 null 表示清空。</summary>
    Task UpdateTrackAsync(MediaItem? item, CancellationToken cancellationToken = default);

    /// <summary>更新播放阶段。</summary>
    void UpdatePlaybackState(PlaybackPhase phase);

    /// <summary>更新播放位置与时长。</summary>
    void UpdatePosition(TimeSpan position, TimeSpan duration);

    /// <summary>更新音量，参数范围为 0..1。</summary>
    void UpdateVolume(double volume);

    /// <summary>更新随机与循环模式。</summary>
    void UpdatePlaybackMode(bool shuffle, RepeatMode repeat);
}
