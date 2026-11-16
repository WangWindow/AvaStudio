using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AvaStudio.Platform;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// Owns native library resolution and the libmpv context. Resolution is app-private and has
/// no system fallback; the video output is configured before <c>mpv_initialize</c> (native
/// window id on Windows, libmpv render API on Linux) so video setup is fixed up front.
/// The render context is released before the core is destroyed by <see cref="DisposeAsync"/>.
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

        if (videoOutput is IMpvRenderVideoOutput renderOutput)
        {
            // Linux 输出由应用自己绘制视频帧，不创建原生窗口。
            // OpenGL 路径可以保留硬件解码；软件路径必须使用 CPU 可读帧。
            SetOption(context, "vo", "libmpv");
            SetOption(context, "hwdec", renderOutput.UsesOpenGl ? "auto-safe" : "no");
        }
        else if (videoOutput is { IsAvailable: true } output && output.WindowId != 0)
        {
            SetOption(context, "hwdec", "auto-safe");
            SetOption(context, "vo", "gpu");
            SetOption(context, "wid", output.WindowId.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            SetOption(context, "hwdec", "auto-safe");
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
