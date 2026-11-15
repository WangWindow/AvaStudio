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
}
