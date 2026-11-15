namespace AvaStudio.Playback;

public enum PlaybackPhase
{
    Idle,
    Opening,
    Playing,
    Paused,
    Stopped,
    Ended,
    Failed,
    Closing,
}

public enum PlaybackIntent
{
    Play,
    Pause,
    Stop,
}

public enum MediaKind
{
    Unknown,
    Audio,
    Video,
}

public enum PlaybackLifecycle
{
    Running,
    Closing,
    Closed,
}

public readonly record struct PlaybackCapabilities(bool CanPlay, bool CanPause, bool CanSeek, bool CanStop)
{
    public static PlaybackCapabilities None => default;
}
