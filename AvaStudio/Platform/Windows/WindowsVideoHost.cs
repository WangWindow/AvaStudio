#if WINDOWS
using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AvaStudio.Platform.Windows;

/// <summary>
/// Avalonia <see cref="NativeControlHost"/> that owns the Win32 child window handed to libmpv
/// as <c>wid</c>. The HWND is created only when Avalonia realizes the control and destroyed
/// when it is detached, so the shell controls layout, clipping and z-order.
/// </summary>
public sealed partial class WindowsVideoHost : NativeControlHost
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;

    private readonly Action<long> _handleCreated;
    private readonly Action _handleDestroyed;
    private IntPtr _handle;

    internal WindowsVideoHost(Action<long> handleCreated, Action handleDestroyed)
    {
        _handleCreated = handleCreated;
        _handleDestroyed = handleDestroyed;
    }

    public IntPtr NativeHandle => _handle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IntPtr handle = CreateWindowExW(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0,
            0,
            1,
            1,
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Failed to create the Win32 video child window. Error: "
                + System.Runtime.InteropServices.Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _handle = handle;
        _handleCreated(handle.ToInt64());
        return new PlatformHandle(handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle handle)
    {
        IntPtr native = handle.Handle;
        _handle = IntPtr.Zero;
        _handleDestroyed();
        if (native != IntPtr.Zero)
        {
            _ = DestroyWindow(native);
        }

        base.DestroyNativeControlCore(handle);
    }

    [System.Runtime.InteropServices.LibraryImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr handle);
}
#endif
