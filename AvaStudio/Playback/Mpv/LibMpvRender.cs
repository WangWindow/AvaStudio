#if LINUX
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// Linux 视频输出使用的最小 libmpv render API 封装。渲染上下文使用软件后端，
/// 把画面写入调用方提供的 BGRA 缓冲区。
/// </summary>
internal static partial class LibMpvRender
{
    // 与 mpv/render.h 中的 mpv_render_param_type 保持一致。
    internal const int ParamInvalid = 0;
    internal const int ParamApiType = 1;
    internal const int ParamOpenGlInitParams = 2;
    internal const int ParamOpenGlFbo = 3;
    internal const int ParamFlipY = 4;
    internal const int ParamX11Display = 8;
    internal const int ParamWlDisplay = 9;
    internal const int ParamAdvancedControl = 10;
    internal const int ParamBlockForTargetTime = 12;
    internal const int ParamSwSize = 17;
    internal const int ParamSwFormat = 18;
    internal const int ParamSwStride = 19;
    internal const int ParamSwPointer = 20;

    internal const string ApiTypeOpenGl = "opengl";
    internal const string ApiTypeSoftware = "sw";

    // Avalonia 的 Bgra8888 与 bgr0 的字节顺序一致；第 4 字节是无用数据，使用不透明 Alpha。
    internal const string SoftwareFormatBgr0 = "bgr0";

    [LibraryImport(LibMpv.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_render_context_create(
        out IntPtr renderContext,
        IntPtr mpvContext,
        IntPtr parameters);

    [LibraryImport(LibMpv.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void mpv_render_context_set_update_callback(
        IntPtr renderContext,
        IntPtr callback,
        IntPtr callbackContext);

    [LibraryImport(LibMpv.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_render_context_render(
        IntPtr renderContext,
        IntPtr parameters);

    [LibraryImport(LibMpv.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void mpv_render_context_free(IntPtr renderContext);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    // 对应 mpv_render_param { enum type; void *data; }，数组以 Type == 0 结束。
    public int Type;
    public IntPtr Data;
}

/// <summary>对应 mpv_opengl_init_params：两个指针，顺序与 C ABI 一致。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;
    public IntPtr GetProcAddressContext;
}

/// <summary>对应 mpv_opengl_fbo：目标 FBO 及像素尺寸。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}
#endif
