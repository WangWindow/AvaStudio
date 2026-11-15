using Avalonia.Controls;

namespace AvaStudio.Platform;

/// <summary>
/// Native video surface. <see cref="Surface"/> is a real Avalonia control that the shell can
/// lay out, clip and animate; the platform implementation owns the native child window behind
/// it. The host must be realized (attached to a window) before <c>mpv_initialize</c> so the
/// <c>wid</c> option is fixed up front. The surface stays in the visual tree while hidden so
/// the native handle is not destroyed and can be reused.
/// </summary>
public interface IVideoOutput : IDisposable
{
    /// <summary>Avalonia control that hosts the native video window.</summary>
    Control Surface { get; }

    /// <summary>Native window id passed to libmpv as <c>wid</c>; 0 until realized/unavailable.</summary>
    long WindowId { get; }

    bool IsAvailable { get; }

    /// <summary>Raised on the UI thread when the native child window has been created.</summary>
    event EventHandler? HandleCreated;
}
