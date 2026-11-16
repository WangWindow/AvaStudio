using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AvaStudio.Platform.Windows;

internal static partial class WindowsWindowAppearance
{
    /// <summary>参与窗口最大化尺寸计算的消息。</summary>
    private const uint WmGetMinMaxInfo = 0x0024;

    /// <summary>客户区尺寸计算；无边框窗口需要把最大化客户区对齐到显示器工作区。</summary>
    private const uint WmNcCalcSize = 0x0083;

    /// <summary>没有显式显示器句柄时，取窗口最近的显示器。</summary>
    private const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>可调整大小的窗口边框；系统 Aero Snap / 贴边布局依赖此样式。</summary>
    private const uint WsThickFrame = 0x00040000;

    /// <summary>最大化按钮样式；Windows 11 的悬停 Snap Layouts 依赖它。</summary>
    private const uint WsMaximizeBox = 0x00010000;

    /// <summary>最小化按钮样式。</summary>
    private const uint WsMinimizeBox = 0x00020000;

    /// <summary>系统菜单样式。</summary>
    private const uint WsSysMenu = 0x00080000;

    /// <summary>播放页系统全屏期间不应用无边框最大化客户区补丁。</summary>
    private static bool _playerFullScreen;

    /// <summary>由播放页在进入/退出系统全屏时调用。</summary>
    public static void SetPlayerFullScreen(bool isFullScreen) => _playerFullScreen = isFullScreen;

    public static void Apply(Window window)
    {
        // 请求原生 Mica 材质；平台不支持时 Avalonia 使用 TransparencyBackgroundFallback。
        window.TransparencyLevelHint = [WindowTransparencyLevel.Mica];

        // 官方 Avalonia 附加属性；Windows 11 22000+ 生效，旧系统忽略。
        Win32Properties.SetWindowCornerPreference(window, Win32Properties.WindowCornerPreference.Round);

        // 构造阶段 PlatformImpl 可能尚未就绪；窗口显示后再挂一次，确保钩子生效。
        AttachHooks(window);
        window.Opened += OnWindowOpened;
    }

    private static void OnWindowOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            AttachHooks(window);
        }
    }

    private static void AttachHooks(Window window)
    {
        // 先移除再添加，避免构造阶段和 Opened 阶段重复注册同一个回调。
        Win32Properties.RemoveWindowStylesCallback(window, SetWindowStyles);
        Win32Properties.AddWindowStylesCallback(window, SetWindowStyles);

        Win32Properties.RemoveWndProcHookCallback(window, OnWndProcHook);
        Win32Properties.AddWndProcHookCallback(window, OnWndProcHook);
    }

    // WindowDecorations=None 会移除 WS_THICKFRAME，导致拖动到屏幕边缘时
    // Windows 不触发 Aero Snap，最大化按钮也没有 Snap Layouts 命中区。
    // 这里补回最小窗口样式；窗口外观仍由自绘标题栏负责。
    private static (uint style, uint exStyle) SetWindowStyles(uint style, uint exStyle) =>
        (style | WsThickFrame | WsMaximizeBox | WsMinimizeBox | WsSysMenu, exStyle);

    private static IntPtr OnWndProcHook(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        // 全屏交给 Avalonia/Windows 标准路径处理，不能再用最大化客户区补丁。
        if (_playerFullScreen)
        {
            return IntPtr.Zero;
        }

        if (msg == WmNcCalcSize)
        {
            if (wParam != 1 || !IsZoomed(hWnd) || !TryGetWorkArea(hWnd, out var workArea))
            {
                return IntPtr.Zero;
            }

            // NCCALCSIZE_PARAMS.rgrc[0] 位于 lParam 起始处，坐标为屏幕坐标。
            // WindowDecorations=None 时 Avalonia 不调整客户区，这里把客户区直接对齐工作区。
            // 不能按“窗口是否覆盖显示器”判断全屏：无任务栏的副屏上工作区等于显示器，
            // 最大化窗口也会覆盖显示器，跳过对齐会让客户区多出边框并被屏幕裁掉。
            var clientRect = new Rect
            {
                Left = workArea.rcWork.Left,
                Top = workArea.rcWork.Top,
                Right = workArea.rcWork.Right,
                Bottom = workArea.rcWork.Bottom,
            };
            Marshal.StructureToPtr(clientRect, lParam, fDeleteOld: false);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        if (!TryGetWorkArea(hWnd, out var monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // ptMaxPosition 是相对显示器原点的偏移；ptMaxSize 是工作区尺寸。
        minMaxInfo.ptMaxPosition.X = monitorInfo.rcWork.Left - monitorInfo.rcMonitor.Left;
        minMaxInfo.ptMaxPosition.Y = monitorInfo.rcWork.Top - monitorInfo.rcMonitor.Top;
        minMaxInfo.ptMaxSize.X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
        minMaxInfo.ptMaxSize.Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
        handled = true;
        return IntPtr.Zero;
    }

    private static bool TryGetWorkArea(IntPtr hWnd, out MonitorInfo monitorInfo)
    {
        var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            monitorInfo = default;
            return false;
        }

        monitorInfo = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref monitorInfo);
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }
}
