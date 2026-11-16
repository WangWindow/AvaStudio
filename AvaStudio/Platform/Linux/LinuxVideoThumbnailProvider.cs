#if LINUX
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaStudio.Playback.Mpv;
using AvaStudio.Services.Library;

namespace AvaStudio.Platform.Linux;

/// <summary>
/// Linux 视频缩略图提取器：创建一次性的 libmpv 上下文，跳到视频约 10% 的位置，
/// 用软件渲染 API 取一帧并编码为 PNG。
/// </summary>
/// <remarks>
/// 资料库扫描可能并行读取多个文件，因此用信号量限制同时存在的 libmpv 上下文数量。
/// 缩略图属于可选内容：任何解析、解码或编码失败都返回 null，不中断资料库扫描。
/// </remarks>
public sealed class LinuxVideoThumbnailProvider : IVideoThumbnailProvider
{
    /// <summary>单次缩略图提取的总超时时间。</summary>
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(8);

    /// <summary>同时解码的视频数量；libmpv 每个上下文都有独立的解码线程。</summary>
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);

    /// <inheritdoc />
    public byte[]? GetThumbnail(string videoPath, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || width <= 0 || height <= 0)
        {
            return null;
        }

        if (!File.Exists(videoPath))
        {
            return null;
        }

        if (!DecodeGate.Wait(ExtractionTimeout))
        {
            return null;
        }

        try
        {
            return ExtractThumbnail(videoPath, width, height);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or OutOfMemoryException)
        {
            Debug.WriteLine($"[Video] Linux 视频缩略图提取失败：{exception.Message}");
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static byte[]? ExtractThumbnail(string videoPath, int width, int height)
    {
        MpvRuntime.EnsureNativeLibraryLoaded();
        IntPtr context = LibMpv.mpv_create();
        if (context == IntPtr.Zero)
        {
            return null;
        }

        MpvSoftwareRenderer? renderer = null;
        try
        {
            Configure(context);
            int error = LibMpv.mpv_initialize(context);
            if (error < 0)
            {
                return null;
            }

            renderer = new MpvSoftwareRenderer(context);
            using var frameReady = new ManualResetEventSlim(false);
            renderer.Start(() => frameReady.Set());

            using (MpvArgv argv = MpvArgv.Create("loadfile", videoPath, "replace"))
            {
                if (LibMpv.mpv_command_async(context, 0, argv.Pointer) < 0)
                {
                    return null;
                }
            }

            var deadline = Stopwatch.StartNew();
            double duration = 0;
            if (!WaitForFileLoaded(context, deadline, out duration))
            {
                return null;
            }

            if (string.IsNullOrEmpty(GetStringProperty(context, "video-format")))
            {
                // 扩展名是视频但实际没有视频轨道（例如纯音频 MP4），不必再等帧超时。
                return null;
            }

            // 开头经常是黑帧或片头；跳转到约 10% 位置再取帧。
            var target = duration > 1 ? Math.Clamp(duration * 0.1, 0, duration - 0.1) : 0;
            if (target > 0)
            {
                frameReady.Reset();
                using MpvArgv seek = MpvArgv.Create(
                    "seek",
                    target.ToString("0.###", CultureInfo.InvariantCulture),
                    "absolute");
                if (LibMpv.mpv_command_async(context, 0, seek.Pointer) < 0)
                {
                    return null;
                }
            }

            if (!WaitForFrame(frameReady, deadline))
            {
                return null;
            }

            using var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            using (var framebuffer = bitmap.Lock())
            {
                if (!renderer.Render(framebuffer.Address, width, height, framebuffer.RowBytes))
                {
                    return null;
                }
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            return stream.ToArray();
        }
        finally
        {
            // 必须先释放渲染上下文，再销毁 mpv 核心。
            renderer?.Dispose();
            LibMpv.mpv_terminate_destroy(context);
        }
    }

    /// <summary>配置一次性播放上下文：只解码视频，不创建音频输出或打开窗口。</summary>
    private static void Configure(IntPtr context)
    {
        SetOption(context, "vo", "libmpv");
        SetOption(context, "audio", "no");
        SetOption(context, "hwdec", "no");
        SetOption(context, "terminal", "no");
        SetOption(context, "input-default-bindings", "no");
        SetOption(context, "osc", "no");
        SetOption(context, "idle", "yes");
        SetOption(context, "keep-open", "no");
        SetOption(context, "cache", "no");
        SetOption(context, "vd-lavc-threads", "1");
    }

    /// <summary>等待文件加载并读取时长；超时、出错或没有视频轨道时返回 false。</summary>
    private static bool WaitForFileLoaded(IntPtr context, Stopwatch deadline, out double duration)
    {
        duration = 0;
        while (deadline.Elapsed < ExtractionTimeout)
        {
            IntPtr eventPointer = LibMpv.mpv_wait_event(context, 0.1);
            if (eventPointer == IntPtr.Zero)
            {
                continue;
            }

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            switch ((MpvEventId)mpvEvent.EventId)
            {
                case MpvEventId.FileLoaded:
                    duration = GetDoubleProperty(context, "duration");
                    return true;
                case MpvEventId.EndFile:
                case MpvEventId.Shutdown:
                    return false;
            }
        }

        return false;
    }

    /// <summary>等待 libmpv 通知有新视频帧；总时长不超过提取超时。</summary>
    private static bool WaitForFrame(ManualResetEventSlim frameReady, Stopwatch deadline)
    {
        while (deadline.Elapsed < ExtractionTimeout)
        {
            var remaining = ExtractionTimeout - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (frameReady.Wait(TimeSpan.FromMilliseconds(50)))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetOption(IntPtr context, string name, string value)
    {
        int error = LibMpv.mpv_set_option_string(context, name, value);
        if (error < 0)
        {
            throw new InvalidOperationException(
                $"libmpv 选项 {name} 设置失败：{LibMpv.ErrorString(error)}。");
        }
    }

    private static double GetDoubleProperty(IntPtr context, string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            int error = LibMpv.mpv_get_property(context, name, LibMpv.FormatDouble, buffer);
            return error < 0 ? 0 : Marshal.PtrToStructure<double>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读取字符串属性；属性不存在时返回空字符串。</summary>
    private static string GetStringProperty(IntPtr context, string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            int error = LibMpv.mpv_get_property(context, name, LibMpv.FormatString, buffer);
            if (error < 0)
            {
                return string.Empty;
            }

            IntPtr value = Marshal.ReadIntPtr(buffer);
            if (value == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUTF8(value) ?? string.Empty;
            }
            finally
            {
                LibMpv.mpv_free(value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
#endif
