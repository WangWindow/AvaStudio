using System;
namespace AvaStudio.Playback;

/// <summary>
/// Identity of a local file handed to the playback engine. The raw path is never part of
/// user-facing failure text; <see cref="DisplayName"/> is safe to show in the UI.
/// </summary>
public sealed record LocalMediaSource
{
    public LocalMediaSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        string displayName = System.IO.Path.GetFileName(path);
        DisplayName = string.IsNullOrEmpty(displayName) ? path : displayName;
    }

    public string Path { get; }

    public string DisplayName { get; }
}
