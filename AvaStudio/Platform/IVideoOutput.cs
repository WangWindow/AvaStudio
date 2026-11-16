using System;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// Video surface that renders libmpv frames itself through the render API instead of
/// embedding a native child window. The runtime calls <see cref="AttachMpvContext"/> after
/// <c>mpv_initialize</c> and <see cref="DetachMpvContext"/> before the mpv core is destroyed.
/// </summary>
public interface IMpvRenderVideoOutput : IVideoOutput
{
    /// <summary>Whether this surface uses the OpenGL render backend (enables VAAPI/VDPAU interop).</summary>
    bool UsesOpenGl { get; }

    /// <summary>
    /// Attach the initialized libmpv handle; called before the first file is loaded.
    /// The implementation must create its render context before returning.
    /// </summary>
    void AttachMpvContext(IntPtr mpvContext);

    /// <summary>
    /// Release the render context before the mpv core is destroyed. Returns false if the
    /// platform context could not be released in time, in which case the core must not be
    /// destroyed while the render context may still be alive.
    /// </summary>
    Task<bool> ReleaseMpvContextAsync(CancellationToken cancellationToken = default);
}
