using System;
using Avalonia.Controls;

namespace AvaStudio.Platform;

/// <summary>
/// Platform-neutral entry point for optional native window appearance.
/// Non-Windows hosts keep the opaque Fluent fallback declared by the UI.
/// </summary>
public static class WindowAppearance
{
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Windows.WindowsWindowAppearance.Apply(window);
    }

    /// <summary>通知平台层当前是否处于播放页系统全屏，避免无边框最大化补丁介入。</summary>
    public static void SetPlayerFullScreen(bool isFullScreen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Windows.WindowsWindowAppearance.SetPlayerFullScreen(isFullScreen);
    }
}
