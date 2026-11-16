using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaStudio.Playback.Mpv;

namespace AvaStudio.Platform;

/// <summary>
/// 跨平台软件视频输出：通过 libmpv 的软件渲染 API 把视频帧画到 Avalonia 控件。
/// 帧在 UI 线程写入 <see cref="WriteableBitmap"/>，视频是普通 Avalonia 内容，
/// 因此不依赖原生子窗口，点击、双击、全屏和跨屏布局都走标准 Avalonia 路径。
/// </summary>
public sealed class SoftwareVideoOutput : IMpvRenderVideoOutput
{
    // 软件渲染的缩放/色彩转换是 CPU 单线程操作；限制目标尺寸，避免大窗口占满 CPU。
    private const int MaxRenderWidth = 2560;
    private const int MaxRenderHeight = 1440;

    private readonly Border _surface;
    private readonly Image _image;
    private readonly TextBlock _errorText;

    // 保护 _renderer 的挂接与释放：创建可能来自播放线程，释放来自 UI 线程。
    private readonly object _gate = new();

    private MpvSoftwareRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private PixelSize _bitmapSize;
    private int _renderQueued;
    private bool _disposed;

    public SoftwareVideoOutput()
    {
        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _errorText = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
            Foreground = Brushes.White,
            IsVisible = false,
        };

        var grid = new Grid
        {
            Children =
            {
                _image,
                _errorText,
            },
        };

        _surface = new Border
        {
            Background = Brushes.Black,
            ClipToBounds = true,
            Child = grid,
        };

        _surface.SizeChanged += OnSurfaceSizeChanged;
    }

    /// <inheritdoc />
    public Control Surface => _surface;

    /// <summary>软件渲染不依赖平台窗口，始终可用；初始化失败会在播放时报告。</summary>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public bool UsesOpenGl => false;

    /// <summary>创建软件渲染上下文并开始接收帧通知。</summary>
    /// <remarks>可能在播放线程调用；实际绘制统一调度回 UI 线程。</remarks>
    public void AttachMpvContext(IntPtr mpvContext)
    {
        if (mpvContext == IntPtr.Zero || _disposed)
        {
            return;
        }

        // 先创建并发布上下文，再启动帧通知，避免回调先于 _renderer 赋值到达。
        MpvSoftwareRenderer? renderer = null;
        try
        {
            renderer = new MpvSoftwareRenderer(mpvContext);
            MpvSoftwareRenderer? previous;
            lock (_gate)
            {
                if (_disposed)
                {
                    renderer.Dispose();
                    return;
                }

                previous = _renderer;
                _renderer = renderer;
            }

            previous?.Dispose();
            renderer.Start(RequestRender);
            RequestRender();
        }
        catch (Exception exception)
        {
            renderer?.Dispose();
            Debug.WriteLine($"[Video] 初始化视频渲染失败：{exception}");
            ShowError($"无法初始化视频渲染：{exception.Message}");
            throw;
        }
    }

    /// <summary>释放 libmpv 渲染上下文；重复调用无害。</summary>
    public Task<bool> ReleaseMpvContextAsync(CancellationToken cancellationToken = default)
    {
        DetachRenderer();
        return Task.FromResult(true);
    }

    /// <summary>释放最后一帧位图；播放结束/停止后由播放层调用。</summary>
    public void ReleaseFrame()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ReleaseFrame);
            return;
        }

        var bitmap = _bitmap;
        _bitmap = null;
        _bitmapSize = default;
        _image.Source = null;
        bitmap?.Dispose();
    }

    private void DetachRenderer()
    {
        MpvSoftwareRenderer? renderer;
        lock (_gate)
        {
            renderer = _renderer;
            _renderer = null;
        }

        // 在 UI 线程释放时不会与 RenderNextFrame 并发；锁只保护挂接/释放状态。
        renderer?.Dispose();
    }

    /// <summary>停止渲染并释放位图；由视图/播放协调器在退出时调用。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.SizeChanged -= OnSurfaceSizeChanged;
        DetachRenderer();

        var bitmap = _bitmap;
        _bitmap = null;
        _bitmapSize = default;
        _image.Source = null;
        bitmap?.Dispose();
    }

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => RequestRender();

    /// <summary>合并 libmpv 的帧通知，只向 UI 线程投递一次重绘。</summary>
    private void RequestRender()
    {
        if (_disposed || Interlocked.Exchange(ref _renderQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(RenderNextFrame, DispatcherPriority.Render);
    }

    /// <summary>在 UI 线程把当前视频帧写入位图；尺寸变化时重建目标。</summary>
    private void RenderNextFrame()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        if (_disposed)
        {
            return;
        }

        MpvSoftwareRenderer? renderer;
        lock (_gate)
        {
            renderer = _renderer;
        }

        if (renderer is null || _surface.Bounds.Width <= 2 || _surface.Bounds.Height <= 2)
        {
            return;
        }

        if (_errorText.IsVisible)
        {
            _errorText.IsVisible = false;
            _image.IsVisible = true;
        }

        var topLevel = TopLevel.GetTopLevel(_surface);
        var scaling = topLevel?.RenderScaling ?? 1.0;
        var (width, height) = ComputeRenderSize(_surface.Bounds.Width, _surface.Bounds.Height, scaling);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureBitmap(width, height);

        try
        {
            using var framebuffer = _bitmap!.Lock();
            _ = renderer.Render(framebuffer.Address, width, height, framebuffer.RowBytes);
            _image.InvalidateVisual();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Video] 绘制视频帧失败：{exception.Message}");
        }
    }

    /// <summary>确保位图尺寸与当前控件像素尺寸一致；只在尺寸变化时重建。</summary>
    private void EnsureBitmap(int width, int height)
    {
        var size = new PixelSize(width, height);
        if (_bitmap is not null && _bitmapSize == size)
        {
            return;
        }

        var bitmap = new WriteableBitmap(
            size,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        var previous = _bitmap;
        _bitmap = bitmap;
        _bitmapSize = size;
        _image.Source = bitmap;
        previous?.Dispose();
    }

    /// <summary>按控件 DIP 尺寸和缩放计算渲染目标，限制最大尺寸并取偶数宽高。</summary>
    private static (int Width, int Height) ComputeRenderSize(double width, double height, double scaling)
    {
        var pixelWidth = Math.Max(1, (int)Math.Round(width * scaling));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * scaling));

        var limit = Math.Min(
            1.0,
            Math.Min(MaxRenderWidth / (double)pixelWidth, MaxRenderHeight / (double)pixelHeight));
        if (limit < 1.0)
        {
            pixelWidth = Math.Max(1, (int)Math.Round(pixelWidth * limit));
            pixelHeight = Math.Max(1, (int)Math.Round(pixelHeight * limit));
        }

        // 4 字节 BGRA 行本身满足像素对齐；取偶数宽高，让软件缩放器面对稳定的对齐目标。
        pixelWidth &= ~1;
        pixelHeight &= ~1;
        return (Math.Max(2, pixelWidth), Math.Max(2, pixelHeight));
    }

    /// <summary>把渲染失败信息显示在视频区域内，避免只留下黑屏。</summary>
    private void ShowError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _errorText.Text = message;
            _errorText.IsVisible = true;
            _image.IsVisible = false;
        });
    }
}
