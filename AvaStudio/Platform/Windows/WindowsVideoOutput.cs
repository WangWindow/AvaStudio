#if WINDOWS
using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AvaStudio.Platform.Windows;

/// <summary>
/// Windows video output: an Avalonia <see cref="WindowsVideoHost"/> surface whose native child
/// HWND becomes libmpv's <c>wid</c>. No HWND is created until the control is realized.
/// </summary>
public sealed class WindowsVideoOutput : IVideoOutput
{
    private readonly WindowsVideoHost _host;
    private long _windowId;

    public WindowsVideoOutput()
    {
        _host = new WindowsVideoHost(OnHandleCreated, OnHandleDestroyed);
    }

    public Control Surface => _host;

    public long WindowId => Volatile.Read(ref _windowId);

    public bool IsAvailable => WindowId != 0;

    public event EventHandler? HandleCreated;

    /// <summary>
    /// Detaches the host from its parent so NativeControlHost destroys the child HWND. Must be
    /// called on the UI thread while the surface is in a visual tree.
    /// </summary>
    public void Dispose()
    {
        switch (_host.Parent)
        {
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl contentControl:
                contentControl.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(_host);
                break;
            default:
                break;
        }
    }

    private void OnHandleCreated(long windowId)
    {
        Volatile.Write(ref _windowId, windowId);
        HandleCreated?.Invoke(this, EventArgs.Empty);
    }

    private void OnHandleDestroyed()
    {
        Volatile.Write(ref _windowId, 0);
    }
}
#endif
