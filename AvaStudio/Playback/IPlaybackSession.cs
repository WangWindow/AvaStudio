using System.Threading.Tasks;
using System.Threading;
using System;
namespace AvaStudio.Playback;

/// <summary>
/// UI-facing media contract. Implementations raise <see cref="SnapshotChanged"/> through
/// their injected dispatcher; the event loop itself never calls user callbacks.
/// </summary>
public interface IPlaybackSession : IAsyncDisposable
{
    PlaybackSnapshot Snapshot { get; }

    event EventHandler<PlaybackSnapshotChangedEventArgs>? SnapshotChanged;

    Task OpenAsync(LocalMediaSource source, CancellationToken cancellationToken = default);

    Task PlayAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default);

    Task CloseAsync();
}
