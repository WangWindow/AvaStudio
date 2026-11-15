namespace AvaStudio.Playback;

public enum PlaybackFailureKind
{
    Unknown,
    OpenFailed,
    PlaybackFailed,
}

public sealed record PlaybackFailure(PlaybackFailureKind Kind, string Message);
