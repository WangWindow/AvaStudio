using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AvaStudio.Platform.Windows;

/// <summary>
/// Windows 原生视频输出：Avalonia <see cref="NativeControlHost"/> 提供一个 Win32 子窗口，
/// 交给 libmpv 作为 <c>wid</c>。libmpv 的 GPU/D3D11 输出直接画在这个子窗口里，
/// 视频帧不经过 Avalonia 纹理，因此是零拷贝路径。
/// </summary>
public sealed class WindowsNativeVideoOutput : INativeVideoOutput
{
    private readonly WindowsNativeVideoHost _host;
    private long _windowId;

    public WindowsNativeVideoOutput()
    {
        _host = new WindowsNativeVideoHost(OnHandleCreated, OnHandleDestroyed);
        _host.SurfaceClicked += (_, _) => SurfaceClicked?.Invoke(this, EventArgs.Empty);
        _host.SurfaceDoubleClicked += (_, _) => SurfaceDoubleClicked?.Invoke(this, EventArgs.Empty);
        _host.SurfacePointerMoved += (_, _) => SurfacePointerMoved?.Invoke(this, EventArgs.Empty);
    }

    public Control Surface => _host;

    public long WindowId => Volatile.Read(ref _windowId);

    public bool IsAvailable => WindowId != 0;

    public event EventHandler? HandleCreated;

    public event EventHandler? SurfaceClicked;

    public event EventHandler? SurfaceDoubleClicked;

    public event EventHandler? SurfacePointerMoved;

    /// <summary>释放原生子窗口；在 UI 线程从可视树移除宿主。</summary>
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

/// <summary>
/// 创建交给 libmpv 的 Win32 子窗口，并在 mpv 创建自己的子窗口后接管鼠标手势。
/// </summary>
public sealed partial class WindowsNativeVideoHost : NativeControlHost
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;

    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const int GwlWndProc = -4;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;

    private static readonly Dictionary<IntPtr, WindowsNativeVideoHost> HostsByChild = [];
    private static readonly Lock HostsLock = new();

    private readonly Action<long> _handleCreated;
    private readonly Action _handleDestroyed;
    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _childPollTimer;

    private IntPtr _handle;
    private IntPtr _mpvChild;
    private IntPtr _wndProcTarget;
    private IntPtr _oldWndProc;
    private bool _suppressNextButtonUp;
    private int _pointerMoveQueued;

    internal WindowsNativeVideoHost(Action<long> handleCreated, Action handleDestroyed)
    {
        _handleCreated = handleCreated;
        _handleDestroyed = handleDestroyed;

        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clickTimer.Tick += OnClickTimerTick;

        // libmpv 在宿主窗口下创建自己的 mpv 子窗口；轮询并接管它的 WndProc，
        // 这样原生视频也能使用单击/双击手势，且窗口重建后能重新挂接。
        _childPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _childPollTimer.Tick += OnChildPollTimerTick;
    }

    public IntPtr NativeHandle => _handle;

    public event EventHandler? SurfaceClicked;

    public event EventHandler? SurfaceDoubleClicked;

    public event EventHandler? SurfacePointerMoved;

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
                "无法创建 Win32 视频子窗口，错误码："
                + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _handle = handle;
        _handleCreated(handle.ToInt64());
        _childPollTimer.Start();
        return new PlatformHandle(handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle handle)
    {
        _childPollTimer.Stop();
        _clickTimer.Stop();
        RestoreChildWndProc();

        IntPtr native = handle.Handle;
        _handle = IntPtr.Zero;
        _handleDestroyed();
        if (native != IntPtr.Zero)
        {
            _ = DestroyWindow(native);
        }

        base.DestroyNativeControlCore(handle);
    }

    private unsafe void OnChildPollTimerTick(object? sender, EventArgs e)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        IntPtr child = FindWindowExW(_handle, IntPtr.Zero, "mpv", null);
        if (child == IntPtr.Zero)
        {
            return;
        }

        if (child != _mpvChild)
        {
            // 让 mpv 子窗口对鼠标透明，点击落到我们创建的宿主窗口；
            // 直接给 mpv 线程创建的窗口挂钩 WndProc 在跨线程时不可靠。
            long exStyle = GetWindowLongPtrW(child, GwlExStyle).ToInt64();
            _ = SetWindowLongPtrW(child, GwlExStyle, (IntPtr)(exStyle | WsExTransparent));
            _mpvChild = child;
        }

        if (_oldWndProc == IntPtr.Zero)
        {
            // Static 控件通常把鼠标消息交给父窗口，因此挂父窗口的 WndProc；
            // 如果父窗口不可用则退回宿主本身。两者都继续链到原 WndProc。
            _wndProcTarget = GetParent(_handle);
            if (_wndProcTarget == IntPtr.Zero)
            {
                _wndProcTarget = _handle;
            }

            lock (HostsLock)
            {
                HostsByChild[_wndProcTarget] = this;
            }

            _oldWndProc = SetWindowLongPtrW(
                _wndProcTarget,
                GwlWndProc,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ChildWndProc);
        }
    }

    private void RestoreChildWndProc()
    {
        if (_wndProcTarget != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
        {
            lock (HostsLock)
            {
                HostsByChild.Remove(_wndProcTarget);
            }

            _ = SetWindowLongPtrW(_wndProcTarget, GwlWndProc, _oldWndProc);
        }

        _mpvChild = IntPtr.Zero;
        _wndProcTarget = IntPtr.Zero;
        _oldWndProc = IntPtr.Zero;
    }

    private void HandleNativeButtonUp()
    {
        // WM_LBUTTONDBLCLK 之后通常还会再来一个 WM_LBUTTONUP，必须忽略，
        // 否则双击会先触发全屏、再延迟触发一次播放/暂停。
        if (_suppressNextButtonUp)
        {
            _suppressNextButtonUp = false;
            return;
        }

        // 有些 mpv 子窗口类不带 CS_DBLCLKS，收不到 WM_LBUTTONDBLCLK；
        // 这里用 250ms 窗口自行区分两次单击和一次双击。
        if (_clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            SurfaceDoubleClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        _clickTimer.Start();
    }

    private void HandleNativeDoubleClick()
    {
        _clickTimer.Stop();
        _suppressNextButtonUp = true;
        SurfaceDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnClickTimerTick(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        SurfaceClicked?.Invoke(this, EventArgs.Empty);
    }

    private void HandleNativePointerMoved()
    {
        Interlocked.Exchange(ref _pointerMoveQueued, 0);
        SurfacePointerMoved?.Invoke(this, EventArgs.Empty);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr ChildWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        WindowsNativeVideoHost? host = null;
        IntPtr oldProc = IntPtr.Zero;
        lock (HostsLock)
        {
            if (HostsByChild.TryGetValue(hwnd, out host))
            {
                oldProc = host._oldWndProc;
            }
        }

        if (host is not null)
        {
            if (msg == WmMouseMove)
            {
                if (Interlocked.Exchange(ref host._pointerMoveQueued, 1) == 0)
                {
                    Dispatcher.UIThread.Post(host.HandleNativePointerMoved, DispatcherPriority.Input);
                }
            }
            else if (msg == WmLeftButtonUp)
            {
                Dispatcher.UIThread.Post(host.HandleNativeButtonUp, DispatcherPriority.Input);
            }
            else if (msg == WmLeftButtonDoubleClick)
            {
                Dispatcher.UIThread.Post(host.HandleNativeDoubleClick, DispatcherPriority.Input);
            }
        }

        return oldProc == IntPtr.Zero
            ? DefWindowProcW(hwnd, msg, wParam, lParam)
            : CallWindowProcW(oldProc, hwnd, msg, wParam, lParam);
    }

    [LibraryImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        StringMarshalling = StringMarshalling.Utf16,
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr handle);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowExW(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtrW(IntPtr handle, int index, IntPtr newValue);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtrW(IntPtr handle, int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetParent(IntPtr handle);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial IntPtr CallWindowProcW(
        IntPtr previousProc,
        IntPtr handle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProcW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
