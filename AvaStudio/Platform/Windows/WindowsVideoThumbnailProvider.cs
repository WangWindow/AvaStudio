#if WINDOWS
using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaStudio.Services.Library;

namespace AvaStudio.Platform.Windows;

/// <summary>
/// 使用 Windows Shell 的 <c>IShellItemImageFactory</c> 从视频文件中提取缩略图。
/// Shell 会调用系统已注册的缩略图处理器（视频解码器）取帧并缩放，因此不需要额外引入解码库；
/// 当系统没有对应处理器、文件损坏或缩略图缓存不可用时返回 null，
/// 由资料库按“没有封面”处理，绝不让扫描因缩略图失败而中断。
/// </summary>
public sealed partial class WindowsVideoThumbnailProvider : IVideoThumbnailProvider
{
    /// <summary>
    /// 只接受真实缩略图；没有缩略图处理器时直接失败，避免回退成文件类型图标。
    /// 这里刻意不传 <c>SIIGBF_BIGGERSIZEOK</c>：实测该标志会让 Shell 返回 768px 级别的
    /// 缓存图，PNG 体积成倍增加，而界面只按 320px 宽度解码，得不偿失。
    /// </summary>
    private const int SiigbfThumbnailOnly = 0x00000008;

    /// <summary>BGRA 像素、无压缩的 DIB 格式。</summary>
    private const uint BiRgb = 0;

    /// <summary>DIB 使用调用方提供的像素缓冲区。</summary>
    private const uint DibRgbColors = 0;

    /// <inheritdoc />
    public byte[]? GetThumbnail(string videoPath, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || width <= 0 || height <= 0)
        {
            return null;
        }

        var hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var result = SHCreateItemFromParsingName(videoPath, IntPtr.Zero, in iid, out var factory);
            if (result != 0 || factory is null)
            {
                return null;
            }

            try
            {
                // Shell 以 cx/cy 中较大者为上限、按原始宽高比缩放：
                // 16:9 视频得到 320x180，其它比例保持画面比例（界面用 UniformToFill 裁剪）。
                var size = new SIZE { cx = width, cy = height };
                result = factory.GetImage(
                    size,
                    SiigbfThumbnailOnly,
                    out hBitmap);
                if (result != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }
            }
            finally
            {
                // 及时释放 Shell 项，避免扫描大量视频时累积 COM 引用。
                // 本文件只在 WINDOWS 编译常量下参与编译，但项目 TFM 未限定平台，
                // 分析器无法推断该分支，故在此局部关闭平台兼容性警告。
#pragma warning disable CA1416
                Marshal.ReleaseComObject(factory);
#pragma warning restore CA1416
            }

            return ConvertToPng(hBitmap);
        }
        catch (Exception exception) when (
            exception is COMException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or OutOfMemoryException)
        {
            // 缩略图是可选内容：编解码器缺失、文件损坏或 Avalonia 渲染后端
            // 尚未就绪时按无封面处理，扫描继续。
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(hBitmap);
            }
        }
    }

    /// <summary>把 Shell 返回的 HBITMAP 转换为 PNG 字节；失败时返回 null。</summary>
    private static byte[]? ConvertToPng(IntPtr hBitmap)
    {
        var source = default(BITMAP);
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), ref source) == 0)
        {
            return null;
        }

        var width = source.bmWidth;
        var height = source.bmHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var pixels = ReadPixels(hBitmap, width, height);
        if (pixels is null)
        {
            return null;
        }

        // 缩略图帧没有透明度语义，统一按不透明位图处理，避免保存出半透明黑边。
        using var target = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using (var buffer = target.Lock())
        {
            var rowBytes = width * 4;
            var copyBytes = Math.Min(rowBytes, buffer.RowBytes);
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    pixels,
                    y * rowBytes,
                    IntPtr.Add(buffer.Address, y * buffer.RowBytes),
                    copyBytes);
            }
        }

        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>
    /// 通过 GetDIBits 把 HBITMAP 的像素复制为自上而下的 BGRA 数组。
    /// 负的 <c>biHeight</c> 表示行序与屏幕坐标一致，可省去逐行翻转。
    /// </summary>
    private static byte[]? ReadPixels(IntPtr hBitmap, int width, int height)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BiRgb,
        };
        var info = new BITMAPINFO { bmiHeader = header, bmiColors = 0 };
        var stride = width * 4;
        var pixels = new byte[stride * height];

        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // 按文档要求，位图不应在调用 GetDIBits 时被选入 DC；
            // 这里只用 DC 提供颜色格式上下文（32 位 BI_RGB 无需调色板）。
            int lines;
            unsafe
            {
                fixed (byte* bits = pixels)
                {
                    lines = GetDIBits(
                        deviceContext,
                        hBitmap,
                        0,
                        (uint)height,
                        (IntPtr)bits,
                        ref info,
                        DibRgbColors);
                }
            }

            return lines == 0 ? null : pixels;
        }
        finally
        {
            _ = DeleteDC(deviceContext);
        }
    }

    /// <summary>Shell 缩略图工厂；IID 固定，不需要运行时激活。</summary>
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        /// <summary>获取缩略图；成功时返回 S_OK 并输出 HBITMAP。</summary>
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    /// <summary>Win32 SIZE，字段顺序与原生定义一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    /// <summary>GDI BITMAP 信息，用于读取 Shell 返回位图的真实尺寸。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    /// <summary>DIB 头；GetDIBits 时描述目标像素缓冲区格式。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>BITMAPINFOHEADER 加一个调色板占位项，满足 GetDIBits 的缓冲区长度要求。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    /// <summary>
    /// 创建 Shell 项。字符串固定 UTF-16；返回值是 HRESULT，失败时不抛异常。
    /// 注意：shell32 只导出无后缀的 <c>SHCreateItemFromParsingName</c>（本身即 Unicode 版本），
    /// 因此必须设置 <c>ExactSpelling</c>，否则运行时会按 CharSet 寻找并不存在的 W 后缀入口。
    /// 该入口使用 DllImport，因为 COM 接口封送无法由 LibraryImport 源生成器处理。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(IntPtr hObject, int cbSize, ref BITMAP lpvObject);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);
}
#endif
