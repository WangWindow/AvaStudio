using System.Diagnostics;
using System.Globalization;
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
        ConfigureBase(context);

        if (videoOutput is INativeVideoOutput nativeOutput && nativeOutput.WindowId != 0)
        {
            ConfigureNative(context, nativeOutput.WindowId);
        }
        else if (videoOutput is IMpvRenderVideoOutput renderOutput)
        {
            SetOption(context, "vo", "libmpv");

            if (renderOutput.UsesOpenGl)
            {
                ConfigureGpuRender(context);
            }
            else
            {
                ConfigureSoftwareRender(context);
            }
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

    /// <summary>
    /// 嵌入式 libmpv 的通用配置：不读取用户 mpv.conf/脚本，不创建窗口，
    /// 使用适合单文件播放的保守缓存。
    /// </summary>
    private static void ConfigureBase(IntPtr context)
    {
        SetOption(context, "config", "no");
        SetOption(context, "load-scripts", "no");
        SetOption(context, "ytdl", "no");
        SetOption(context, "idle", "yes");
        SetOption(context, "keep-open", "yes");
        SetOption(context, "force-window", "no");
        SetOption(context, "terminal", "no");
        SetOption(context, "input-default-bindings", "no");
        SetOption(context, "osc", "no");
        SetOption(context, "audio-client-name", "AvaStudio");
        SetOption(context, "save-position-on-quit", "no");
        SetOption(context, "resume-playback", "no");

        // 当前播放入口都是本地文件。默认缓存会预留上百 MiB；
        // 32/8 MiB 足以覆盖 4K 高码率的几秒预读，同时远低于 mpv 默认值。
        SetOption(context, "cache", "no");
        SetOption(context, "demuxer-max-bytes", "32MiB");
        SetOption(context, "demuxer-max-back-bytes", "8MiB");
        SetOption(context, "demuxer-readahead-secs", "2");
        SetOption(context, "cache-secs", "2");

        // 主流 mpv 配置不强制解码线程数；硬解时该值基本不参与，
        // 软件回退时交给 libmpv 按 CPU/分辨率自动决定。
        SetOption(context, "vd-lavc-threads", "0");
        SetOption(context, "audio-buffer", "0.2");
    }

    /// <summary>
    /// 软件 render API 配置：需要 CPU 可读帧，使用 D3D11VA copy-back；
    /// 缩放/抖动由 zimg 完成，输出 full-range RGB 与 Avalonia 位图对齐。
    /// </summary>
    private static void ConfigureSoftwareRender(IntPtr context)
    {
        SetOption(context, "hwdec", "auto-copy-safe");
        SetOption(context, "sws-scaler", "lanczos");
        SetOption(context, "sws-allow-zimg", "yes");
        SetOption(context, "zimg-scaler", "lanczos");
        SetOption(context, "zimg-scaler-param-a", "3");
        SetOption(context, "zimg-dither", "error-diffusion");
        SetOption(context, "video-output-levels", "full");
    }

    /// <summary>
    /// Windows 原生窗口配置：libmpv 的 GPU/D3D11 输出直接渲染到 <c>wid</c>，
    /// 视频帧不经过 Avalonia 纹理，走 D3D11VA 零拷贝硬件解码。
    /// </summary>
    private static void ConfigureNative(IntPtr context, long windowId)
    {
        SetOption(context, "vo", "gpu");
        SetOption(context, "gpu-api", "d3d11");
        SetOption(context, "hwdec", "auto-safe");
        SetOption(context, "wid", windowId.ToString(CultureInfo.InvariantCulture));
        ConfigureGpuQuality(context);
    }

    /// <summary>
    /// GPU/OpenGL 配置：对齐 mpv 的 high-quality profile（ewa_lanczossharp、
    /// antiring、deband），并补上 8-bit FBO 的抖动与 HDR→SDR 色调映射。
    /// </summary>
    private static void ConfigureGpuRender(IntPtr context)
    {
        // Windows 的 ANGLE/OpenGL 与 D3D11VA copy-back 在 4K 下会掉帧，
        // 保持软件解码；Linux 原生 GLX/EGL 可以走 VAAPI 零拷贝解码。
        SetOption(context, "hwdec", OperatingSystem.IsLinux() ? "auto-safe" : "no");
        ConfigureGpuQuality(context);
    }

    /// <summary>GPU 路径共用的高质量缩放、去色带、抖动和 HDR→SDR 设置。</summary>
    private static void ConfigureGpuQuality(IntPtr context)
    {
        SetOption(context, "scale", "ewa_lanczossharp");
        SetOption(context, "cscale", "ewa_lanczossharp");
        SetOption(context, "dscale", "mitchell");
        SetOption(context, "scale-antiring", "0.6");
        SetOption(context, "cscale-antiring", "0.6");
        SetOption(context, "correct-downscaling", "yes");
        SetOption(context, "linear-downscaling", "yes");
        SetOption(context, "sigmoid-upscaling", "yes");

        SetOption(context, "deband", "yes");
        SetOption(context, "deband-iterations", "2");
        SetOption(context, "deband-threshold", "48");
        SetOption(context, "deband-range", "16");
        SetOption(context, "deband-grain", "0");

        SetOption(context, "dither", "error-diffusion");
        SetOption(context, "error-diffusion", "sierra-lite");
        SetOption(context, "dither-depth", "8");

        // Avalonia FBO 是 8-bit SDR，不做 HDR 直通；HDR 内容映射到 SDR。
        SetOption(context, "tone-mapping", "bt.2390");
        SetOption(context, "hdr-compute-peak", "yes");
        SetOption(context, "hdr-peak-percentile", "99.995");
        SetOption(context, "hdr-contrast-recovery", "0.30");
        SetOption(context, "gamut-mapping-mode", "perceptual");
        SetOption(context, "target-prim", "bt.709");
        SetOption(context, "target-trc", "srgb");
        SetOption(context, "target-peak", "auto");
        SetOption(context, "target-colorspace-hint", "no");
        SetOption(context, "video-output-levels", "full");
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
