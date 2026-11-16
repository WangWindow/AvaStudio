using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// 拥有一个 libmpv 软件渲染上下文。调用方必须串行化 <see cref="Render"/>；
/// 该方法预期在 UI 线程锁定 <see cref="Avalonia.Media.Imaging.WriteableBitmap"/> 后调用。
/// </summary>
internal sealed class MpvSoftwareRenderer : IDisposable
{
    private readonly object _gate = new();

    private IntPtr _renderContext;
    private IntPtr _format;
    private GCHandle _self;
    private Action? _frameAvailable;
    private bool _disposed;

    /// <summary>创建“sw”渲染上下文；必须在 <c>mpv_initialize</c> 之后、加载文件之前调用。</summary>
    public MpvSoftwareRenderer(IntPtr mpvContext)
    {
        if (mpvContext == IntPtr.Zero)
        {
            throw new ArgumentException("libmpv 上下文不能为空。", nameof(mpvContext));
        }

        IntPtr apiType = Marshal.StringToCoTaskMemUTF8(LibMpvRender.ApiTypeSoftware);
        try
        {
            unsafe
            {
                var parameters = stackalloc MpvRenderParam[2];
                parameters[0] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamApiType,
                    Data = apiType,
                };
                parameters[1] = default;

                int error = LibMpvRender.mpv_render_context_create(
                    out _renderContext,
                    mpvContext,
                    (IntPtr)parameters);
                if (error < 0)
                {
                    throw new InvalidOperationException(
                        $"无法创建 libmpv 软件渲染上下文：{LibMpv.ErrorString(error)}。");
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }

        _format = Marshal.StringToCoTaskMemUTF8(LibMpvRender.SoftwareFormatBgr0);
    }

    /// <summary>注册帧通知：libmpv 的回调线程只负责触发，实际绘制由调用方调度。</summary>
    public void Start(Action frameAvailable)
    {
        ArgumentNullException.ThrowIfNull(frameAvailable);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frameAvailable is not null)
            {
                return;
            }

            _frameAvailable = frameAvailable;
            _self = GCHandle.Alloc(this);
            unsafe
            {
                LibMpvRender.mpv_render_context_set_update_callback(
                    _renderContext,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnFrameAvailable,
                    GCHandle.ToIntPtr(_self));
            }
        }
    }

    /// <summary>把当前视频帧渲染到 32 位 BGRA 缓冲区；失败时保留上一帧。</summary>
    public bool Render(IntPtr buffer, int width, int height, int stride)
    {
        if (buffer == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed || _renderContext == IntPtr.Zero)
            {
                return false;
            }

            unsafe
            {
                var size = stackalloc int[2] { width, height };
                nuint strideValue = (nuint)stride;
                int blockForTargetTime = 0;

                var parameters = stackalloc MpvRenderParam[6];
                parameters[0] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamSwSize,
                    Data = (IntPtr)size,
                };
                parameters[1] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamSwFormat,
                    Data = _format,
                };
                parameters[2] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamSwStride,
                    Data = (IntPtr)(&strideValue),
                };
                parameters[3] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamSwPointer,
                    Data = buffer,
                };
                parameters[4] = new MpvRenderParam
                {
                    Type = LibMpvRender.ParamBlockForTargetTime,
                    Data = (IntPtr)(&blockForTargetTime),
                };
                parameters[5] = default;

                int error = LibMpvRender.mpv_render_context_render(_renderContext, (IntPtr)parameters);
                if (error < 0 && error != -2)
                {
                    // -2 是 MPV_ERROR_NOTHING_TO_RENDER：音频/暂停/首帧前属于正常状态。
                    Debug.WriteLine($"[Video] libmpv 视频渲染失败：{LibMpv.ErrorString(error)}");
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>释放渲染上下文；必须在 mpv 核心销毁前调用。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frameAvailable = null;

            if (_renderContext != IntPtr.Zero)
            {
                LibMpvRender.mpv_render_context_set_update_callback(_renderContext, IntPtr.Zero, IntPtr.Zero);
                LibMpvRender.mpv_render_context_free(_renderContext);
                _renderContext = IntPtr.Zero;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            if (_format != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_format);
                _format = IntPtr.Zero;
            }
        }
    }

    /// <summary>libmpv 回调入口；不能在这里调用其他 mpv API，也不能持有托管委托。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFrameAvailable(IntPtr userData)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userData).Target is MpvSoftwareRenderer renderer)
            {
                renderer.NotifyFrameAvailable();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Video] 处理 libmpv 渲染通知失败：{exception.Message}");
        }
    }

    /// <summary>转交帧通知；调用方负责切回 UI 线程。</summary>
    private void NotifyFrameAvailable()
    {
        Action? frameAvailable;
        lock (_gate)
        {
            frameAvailable = _frameAvailable;
        }

        frameAvailable?.Invoke();
    }
}
