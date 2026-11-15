using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using AvaStudio.Platform;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// Owns native library resolution and the libmpv context. Resolution is app-private and has
/// no system fallback; the context is configured before <c>mpv_initialize</c> so the video
/// window id is fixed up front.
/// </summary>
public sealed class MpvRuntime : IDisposable
{
    private static readonly Lock ResolverLock = new();
    private static bool s_resolverInstalled;
    private static IntPtr s_libraryHandle;

    private IntPtr _context;

    private MpvRuntime(IntPtr context, string nativeLibraryPath, string mpvVersion, ulong clientApiVersion)
    {
        _context = context;
        NativeLibraryPath = nativeLibraryPath;
        MpvVersion = mpvVersion;
        ClientApiVersion = clientApiVersion;
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
            return new MpvRuntime(context, libraryPath, version, apiVersion);
        }
        catch
        {
            LibMpv.mpv_terminate_destroy(context);
            throw;
        }
    }

    public void Dispose()
    {
        IntPtr context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (context != IntPtr.Zero)
        {
            LibMpv.mpv_terminate_destroy(context);
        }
    }

    private static void Configure(IntPtr context, IVideoOutput? videoOutput)
    {
        SetOption(context, "idle", "yes");
        SetOption(context, "keep-open", "yes");
        SetOption(context, "terminal", "no");
        SetOption(context, "input-default-bindings", "no");
        SetOption(context, "osc", "no");
        SetOption(context, "hwdec", "auto-safe");
        SetOption(context, "audio-client-name", "AvaStudio");

        if (videoOutput is { IsAvailable: true } output && output.WindowId != 0)
        {
            SetOption(context, "vo", "gpu");
            SetOption(context, "wid", output.WindowId.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
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
