#pragma warning disable CS0067
using System;
using System.Threading;
using System.Threading.Tasks;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Services.Playback;

namespace AvaStudio.Services.MediaTransport;

/// <summary>不支持系统媒体控制时的空实现，所有调用都是无害的 no-op。</summary>
public sealed class NoopMediaTransportService : IMediaTransportService
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
