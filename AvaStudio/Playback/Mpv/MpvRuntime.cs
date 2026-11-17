using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AvaStudio.Platform;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// Owns native library resolution and the libmpv context. Resolution is app-private and has
/// no system fallback; the video output is configured to use the libmpv render API before
/// <c>mpv_initialize</c>, and the render context is released before the core is destroyed by
/// <see cref="DisposeAsync"/>.
/// </summary>
public sealed class MpvRuntime : IAsyncDisposable
{
    private static readonly Lock ResolverLock = new();
    private static bool s_resolverInstalled;
    private static IntPtr s_libraryHandle;

    private readonly IVideoOutput? _videoOutput;

    private IntPtr _context;

    private MpvRuntime(
        IntPtr context,
        string nativeLibraryPath,
        string mpvVersion,
        ulong clientApiVersion,
        IVideoOutput? videoOutput)
    {
        _context = context;
        NativeLibraryPath = nativeLibraryPath;
        MpvVersion = mpvVersion;
        ClientApiVersion = clientApiVersion;
        _videoOutput = videoOutput;
    }

    public string NativeLibraryPath { get; }

    public string MpvVersion { get; }

    public ulong ClientApiVersion { get; }

    internal IntPtr Context => _context;

    public static MpvRuntime Create(IVideoOutput? videoOutput = null, INativeLibraryLocator? nativeLibraryLocator = null)
    {
        INativeLibraryLocator locator = nativeLibraryLocator ?? NativeLibraryLocator.CreateDefault();
        string libraryPath = locator.ResolveLibraryPath();
        EnsureLibraryLoaded(libraryPath);

        IntPtr context = LibMpv.mpv_create();
        if (context == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法创建 libmpv 播放上下文。");
        }

        try
        {
            Configure(context, videoOutput);
            int error = LibMpv.mpv_initialize(context);
            if (error < 0)
            {
                throw new InvalidOperationException($"libmpv 初始化失败：{LibMpv.ErrorString(error)}。");
            }

            string version = GetStringProperty(context, "mpv-version");
            ulong apiVersion = LibMpv.mpv_client_api_version();
            var runtime = new MpvRuntime(context, libraryPath, version, apiVersion, videoOutput);
            if (videoOutput is IMpvRenderVideoOutput renderOutput)
            {
                renderOutput.AttachMpvContext(context);
            }

            return runtime;
        }
        catch
        {
            LibMpv.mpv_terminate_destroy(context);
            throw;
        }
    }

    /// <summary>
    /// 确保应用私有 libmpv 已加载并安装 DllImport 解析器。
    /// 缩略图提取等不经过播放会话的调用也需要先执行这一步。
    /// </summary>
    internal static void EnsureNativeLibraryLoaded()
    {
        INativeLibraryLocator locator = NativeLibraryLocator.CreateDefault();
        EnsureLibraryLoaded(locator.ResolveLibraryPath());
    }

    public async ValueTask DisposeAsync()
    {
        IntPtr context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (context == IntPtr.Zero)
        {
            return;
        }

        if (_videoOutput is IMpvRenderVideoOutput renderOutput)
        {
            bool released;
            try
            {
                released = await renderOutput.ReleaseMpvContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Video] 释放视频渲染上下文失败：{exception}");
                released = false;
            }

            if (!released)
            {
                // 渲染上下文仍可能引用 mpv 核心；此时销毁核心是未定义行为，只能放弃本次清理。
                Debug.WriteLine("[Video] 视频渲染上下文未释放，跳过 mpv 核心销毁。");
                return;
            }
        }

        // mpv_terminate_destroy 可能等待解码/显示线程退出，不阻塞 UI 线程。
        await Task.Run(() => LibMpv.mpv_terminate_destroy(context)).ConfigureAwait(false);
    }

    private static void Configure(IntPtr context, IVideoOutput? videoOutput)
    {
        SetOption(context, "idle", "yes");
        SetOption(context, "keep-open", "yes");
        SetOption(context, "terminal", "no");
        SetOption(context, "input-default-bindings", "no");
        SetOption(context, "osc", "no");
        SetOption(context, "audio-client-name", "AvaStudio");

        // 当前播放入口都是本地文件。libmpv 默认会为解复用缓存预留上百 MiB，
        // 并把大量解码帧留在内存里；这里按本地播放收紧缓存和线程数。
        SetOption(context, "cache", "no");
        SetOption(context, "demuxer-max-bytes", "16MiB");
        SetOption(context, "demuxer-max-back-bytes", "4MiB");
        SetOption(context, "demuxer-readahead-secs", "2");
        SetOption(context, "cache-secs", "2");
        // 4K/HEVC 需要更多解码线程；线程数增加的内存占用远小于硬件回传缓冲。
        SetOption(context, "vd-lavc-threads", "4");
        SetOption(context, "audio-buffer", "0.2");

        // 软件渲染默认的双线性缩放在高分辨率素材上会明显发软；
        // zimg/lanczos 只在渲染目标尺寸变化时工作，额外 CPU 开销可控。
        SetOption(context, "sws-scaler", "lanczos");
        SetOption(context, "sws-allow-zimg", "yes");
        SetOption(context, "zimg-scaler", "lanczos");
        SetOption(context, "zimg-scaler-param-a", "3");
        SetOption(context, "video-output-levels", "full");

        // HDR/10-bit 素材最终输出到 8-bit SDR 位图：用 BT.2390 色调映射、
        // 感知色域映射和误差扩散抖动，避免高光裁剪和渐变色带。
        // 注意：软件 render API 不提供 HDR 直通，HDR 会被映射到 SDR。
        SetOption(context, "tone-mapping", "bt.2390");
        SetOption(context, "hdr-compute-peak", "yes");
        SetOption(context, "gamut-mapping-mode", "perceptual");
        SetOption(context, "target-prim", "bt.709");
        SetOption(context, "target-trc", "srgb");
        SetOption(context, "target-peak", "200");
        SetOption(context, "dither-depth", "8");
        SetOption(context, "zimg-dither", "error-diffusion");
        SetOption(context, "deband", "yes");
        SetOption(context, "deband-iterations", "2");
        SetOption(context, "deband-threshold", "48");
        SetOption(context, "deband-grain", "0");

        // OpenGL render API 使用的 GPU 缩放参数；软件渲染会自动忽略这些选项。
        SetOption(context, "scale", "ewa_lanczossharp");
        SetOption(context, "cscale", "ewa_lanczossoft");
        SetOption(context, "dscale", "mitchell");
        SetOption(context, "correct-downscaling", "yes");
        SetOption(context, "linear-downscaling", "yes");
        SetOption(context, "sigmoid-upscaling", "yes");

        if (videoOutput is IMpvRenderVideoOutput renderOutput)
        {
            // 视频由应用自己绘制成 Avalonia 内容，不创建原生窗口。
            // OpenGL 路径可以保留硬件解码；软件路径必须使用 CPU 可读帧。
            SetOption(context, "vo", "libmpv");
            // 软件 render API 需要 CPU 可读帧：auto-copy-safe 自动选择
            // D3D11VA copy-back 硬件解码，不可用时自动退回软件解码。
            // OpenGL/ANGLE 路径与 D3D11VA 回传组合在 4K 下会掉帧，
            // 因此 GPU 渲染模式仍用软件解码，把 CPU 留给色调映射/缩放。
            SetOption(context, "hwdec", renderOutput.UsesOpenGl ? "no" : "auto-copy-safe");
        }
        else
        {
            // 没有可用的视频表面时只播放音频，避免 libmpv 尝试打开任何窗口。
            SetOption(context, "hwdec", "no");
            SetOption(context, "vo", "null");
        }

        if (string.Equals(Environment.GetEnvironmentVariable("AVASTUDIO_MPV_LOG"), "1", StringComparison.Ordinal))
        {
            _ = LibMpv.mpv_request_log_messages(context, "info");
        }
    }

    private static void SetOption(IntPtr context, string name, string value)
    {
        int error = LibMpv.mpv_set_option_string(context, name, value);
        if (error < 0)
        {
            throw new InvalidOperationException($"libmpv 选项 {name} 设置失败：{LibMpv.ErrorString(error)}。");
        }
    }

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

    private static void EnsureLibraryLoaded(string libraryPath)
    {
        lock (ResolverLock)
        {
            if (s_libraryHandle != IntPtr.Zero)
            {
                return;
            }

            s_libraryHandle = NativeLibrary.Load(libraryPath);
            if (!s_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, ResolveLibrary);
                s_resolverInstalled = true;
            }
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return string.Equals(libraryName, LibMpv.LibraryName, StringComparison.Ordinal)
            ? s_libraryHandle
            : IntPtr.Zero;
    }
}
