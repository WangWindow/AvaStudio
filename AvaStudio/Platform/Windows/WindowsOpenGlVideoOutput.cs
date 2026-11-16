using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using AvaStudio.Playback.Mpv;

namespace AvaStudio.Platform.Windows;

/// <summary>
/// Windows OpenGL 视频输出：用 Avalonia 的 <see cref="OpenGlControlBase"/>（ANGLE/D3D11）
/// 提供当前 GL 上下文，libmpv 通过 render API 把视频直接画到 Avalonia 的离屏 FBO。
/// 相比软件位图，这条路径可以启用 GPU 缩放和 HDR 色调映射。
/// </summary>
/// <remarks>
/// Avalonia 12 的 GL 回调在 UI 线程执行，并且只在回调期间保证上下文 current；
/// 因此渲染上下文的创建、绘制和释放都放在 <c>OnOpenGl*</c> 回调内。
/// 播放层创建 mpv 后会等待 <see cref="AttachMpvContext"/> 完成，确保在加载文件前渲染上下文已就绪。
/// </remarks>
public sealed partial class WindowsOpenGlVideoOutput : OpenGlControlBase, IMpvRenderVideoOutput
{
    private const string ApiTypeOpenGl = "opengl";

    private readonly object _gate = new();

    private IntPtr _mpvContext;
    private IntPtr _renderContext;
    private GlInterface? _gl;
    private bool _glInitialized;
    private bool _disposed;
    private int _releaseRequested;
    private GCHandle _selfHandle;
    private GCHandle _procHandle;
    private TaskCompletionSource<bool>? _attachReady;
    private TaskCompletionSource<bool>? _releaseReady;

    public WindowsOpenGlVideoOutput()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public Control Surface => this;

    /// <summary>OpenGL 渲染依赖 Avalonia 的 GL 后端；初始化失败会在播放时报告。</summary>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public bool UsesOpenGl => true;

    /// <summary>保存 mpv 句柄并等待 GL 渲染上下文创建完成。</summary>
    /// <remarks>
    /// 由播放层在后台线程调用；这里阻塞等待 UI 线程完成一次 GL 渲染回调。
    /// 调用方必须确保不在 UI 线程同步等待，否则会与 GL 回调互相等待。
    /// </remarks>
    public void AttachMpvContext(IntPtr mpvContext)
    {
        if (mpvContext == IntPtr.Zero || _disposed)
        {
            return;
        }

        TaskCompletionSource<bool> ready;
        lock (_gate)
        {
            if (_renderContext != IntPtr.Zero)
            {
                _mpvContext = mpvContext;
                return;
            }

            _mpvContext = mpvContext;
            ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _attachReady = ready;
        }

        // GL 上下文只在 OnOpenGl* 回调内 current；触发一次合成，让 UI 线程创建上下文。
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

        try
        {
            ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_attachReady, ready))
                {
                    _mpvContext = IntPtr.Zero;
                }
            }

            throw new InvalidOperationException("等待 OpenGL 视频渲染上下文超时。");
        }
    }

    /// <summary>在 GL 上下文 current 时释放 mpv 渲染上下文。</summary>
    public async Task<bool> ReleaseMpvContextAsync(CancellationToken cancellationToken = default)
    {
        Task<bool>? existingRelease = null;
        TaskCompletionSource<bool>? release = null;
        lock (_gate)
        {
            if (_renderContext == IntPtr.Zero)
            {
                return true;
            }

            if (_releaseReady is not null)
            {
                existingRelease = _releaseReady.Task;
            }
            else
            {
                release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseReady = release;
                Volatile.Write(ref _releaseRequested, 1);
            }
        }

        if (existingRelease is not null)
        {
            return await existingRelease.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

        try
        {
            return await release!.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("[Video] 等待 OpenGL 渲染上下文释放超时。");
            return false;
        }
    }

    /// <summary>GL 上下文就绪；如果 mpv 已挂接则立即创建渲染上下文。</summary>
    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        _glInitialized = true;

        if (Volatile.Read(ref _mpvContext) != IntPtr.Zero)
        {
            EnsureRenderContext();
        }
    }

    /// <summary>先把离屏 FBO 清为黑色，再让 libmpv 把当前视频帧绘制到同一个 FBO。</summary>
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        _gl = gl;

        if (Interlocked.Exchange(ref _releaseRequested, 0) != 0)
        {
            FreeRenderContext();
            return;
        }

        if (Volatile.Read(ref _mpvContext) != IntPtr.Zero && _renderContext == IntPtr.Zero)
        {
            EnsureRenderContext();
        }

        var (width, height) = GetPixelSize();
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

        if (_renderContext == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            var fbo = new MpvOpenGlFbo
            {
                Fbo = fb,
                Width = width,
                Height = height,
                InternalFormat = 0,
            };
            int flipY = 1;
            int blockForTargetTime = 0;

            var parameters = stackalloc MpvRenderParam[4];
            parameters[0] = new MpvRenderParam
            {
                Type = LibMpvRender.ParamOpenGlFbo,
                Data = (IntPtr)(&fbo),
            };
            parameters[1] = new MpvRenderParam
            {
                Type = LibMpvRender.ParamFlipY,
                Data = (IntPtr)(&flipY),
            };
            parameters[2] = new MpvRenderParam
            {
                Type = LibMpvRender.ParamBlockForTargetTime,
                Data = (IntPtr)(&blockForTargetTime),
            };
            parameters[3] = default;

            int error = LibMpvRender.mpv_render_context_render(_renderContext, (IntPtr)parameters);
            if (error < 0 && error != -2)
            {
                // 无视频帧（音频/空闲）时 mpv 返回 NOTHING_TO_RENDER，这是正常状态。
                Debug.WriteLine($"[Video] OpenGL 视频渲染失败：{LibMpv.ErrorString(error)}");
            }
        }
    }

    /// <summary>GL 上下文销毁前释放 mpv 渲染上下文；mpv 核心仍由播放层异步销毁。</summary>
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        FreeRenderContext();
        _glInitialized = false;
        _gl = null;
    }

    protected override void OnOpenGlLost()
    {
        // 上下文已丢失，无法再安全调用 mpv_render_context_free；保留状态等待释放超时，
        // 渲染层会因拿不到可用上下文而停止视频，下一次初始化会重新创建。
        _glInitialized = false;
        _gl = null;
        Debug.WriteLine("[Video] Avalonia OpenGL 上下文已丢失。");
    }

    /// <summary>停止渲染；mpv 核的释放由播放层通过 <see cref="ReleaseMpvContextAsync"/> 完成。</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_renderContext != IntPtr.Zero)
        {
            Volatile.Write(ref _releaseRequested, 1);
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
        }
    }

    /// <summary>在 GL 上下文 current 时创建 mpv 渲染上下文，并完成挂接等待。</summary>
    private void EnsureRenderContext()
    {
        if (_renderContext != IntPtr.Zero)
        {
            _attachReady?.TrySetResult(true);
            return;
        }

        if (!_glInitialized || _gl is null || Volatile.Read(ref _mpvContext) == IntPtr.Zero)
        {
            return;
        }

        if (!_selfHandle.IsAllocated)
        {
            _selfHandle = GCHandle.Alloc(this);
        }

        if (!_procHandle.IsAllocated)
        {
            _procHandle = GCHandle.Alloc(this);
        }

        IntPtr apiType = Marshal.StringToCoTaskMemUTF8(ApiTypeOpenGl);
        try
        {
            unsafe
            {
                var initParams = new MpvOpenGlInitParams
                {
                    GetProcAddress = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&GetProcAddress,
                    GetProcAddressContext = GCHandle.ToIntPtr(_procHandle),
                };

                var parameters = stackalloc MpvRenderParam[3];
                parameters[0] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamApiType,
                    Data = apiType,
                };
                parameters[1] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamOpenGlInitParams,
                    Data = (IntPtr)(&initParams),
                };
                parameters[2] = default;

                int error = LibMpvRender.mpv_render_context_create(
                    out _renderContext,
                    Volatile.Read(ref _mpvContext),
                    (IntPtr)parameters);
                if (error < 0)
                {
                    Volatile.Write(ref _mpvContext, IntPtr.Zero);
                    _attachReady?.TrySetException(new InvalidOperationException(
                        $"无法创建 OpenGL 视频渲染上下文：{LibMpv.ErrorString(error)}。"));
                    return;
                }

                LibMpvRender.mpv_render_context_set_update_callback(
                    _renderContext,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnFrameAvailable,
                    GCHandle.ToIntPtr(_selfHandle));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }

        _attachReady?.TrySetResult(true);
    }

    /// <summary>释放 mpv 渲染上下文、平台显示连接和回调 GCHandle。</summary>
    private void FreeRenderContext()
    {
        if (_renderContext != IntPtr.Zero)
        {
            LibMpvRender.mpv_render_context_set_update_callback(_renderContext, IntPtr.Zero, IntPtr.Zero);
            LibMpvRender.mpv_render_context_free(_renderContext);
            _renderContext = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        if (_procHandle.IsAllocated)
        {
            _procHandle.Free();
        }

        _releaseReady?.TrySetResult(true);
        _releaseReady = null;
    }

    /// <summary>把控件 DIP 尺寸换算成物理像素，作为 mpv 的 FBO 尺寸。</summary>
    private (int Width, int Height) GetPixelSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return (
            Math.Max(1, (int)Math.Round(Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Round(Bounds.Height * scaling)));
    }

    /// <summary>把 mpv 的帧通知合并成 UI 线程的一次合成请求。</summary>
    private void RequestRender()
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    /// <summary>把 GL 函数名转给 Avalonia 当前上下文；由 mpv 在创建/重配时回调。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetProcAddress(IntPtr userData, IntPtr name)
    {
        if (userData == IntPtr.Zero || name == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var output = GCHandle.FromIntPtr(userData).Target as WindowsOpenGlVideoOutput;
            var procName = Marshal.PtrToStringUTF8(name);
            return procName is null ? IntPtr.Zero : output?._gl?.GetProcAddress(procName) ?? IntPtr.Zero;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Video] 解析 OpenGL 函数失败：{exception.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>libmpv 帧通知回调；不能在回调里调用 mpv 或其他阻塞 API。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFrameAvailable(IntPtr userData)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userData).Target is WindowsOpenGlVideoOutput output)
            {
                output.RequestRender();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Video] 处理 OpenGL 帧通知失败：{exception.Message}");
        }
    }
}
