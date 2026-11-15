using System;
namespace AvaStudio.Playback;

/// <summary>
/// Immutable view of the player engine. Identity is <see cref="Generation"/> plus
/// <see cref="Revision"/>; consumers must discard snapshots from older generations.
/// </summary>
public sealed record PlaybackSnapshot(
    long Generation,
    long Revision,
    LocalMediaSource? Source,
    PlaybackPhase Phase,
    PlaybackIntent Intent,
    TimeSpan Position,
    TimeSpan? Duration,
    PlaybackCapabilities Capabilities,
    double Volume,
    bool IsMuted,
    MediaKind MediaKind,
    bool IsBusy,
    double? Buffering,
    PlaybackFailure? Failure,
    PlaybackLifecycle Lifecycle)
{
    public static PlaybackSnapshot Initial { get; } = new(
        0,
        0,
        null,
        PlaybackPhase.Idle,
        PlaybackIntent.Stop,
        TimeSpan.Zero,
        null,
        PlaybackCapabilities.None,
        100,
        false,
        MediaKind.Unknown,
        false,
        null,
        null,
        PlaybackLifecycle.Running);
}

public sealed class PlaybackSnapshotChangedEventArgs : EventArgs
{
    public PlaybackSnapshotChangedEventArgs(PlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    public PlaybackSnapshot Snapshot { get; }
}
