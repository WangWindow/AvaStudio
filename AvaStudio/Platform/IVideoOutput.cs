using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace AvaStudio.Platform;

/// <summary>
/// 视频输出表面。<see cref="Surface"/> 是普通的 Avalonia 控件，由主窗口布局、裁剪和动画，
/// 不再依赖原生视频子窗口。播放层在创建 libmpv 时通过 <see cref="IMpvRenderVideoOutput"/>
/// 把渲染上下文挂到该表面。
/// </summary>
public interface IVideoOutput : IDisposable
{
    /// <summary>承载视频画面的 Avalonia 控件。</summary>
    Control Surface { get; }

    /// <summary>当前平台是否可以输出视频；不可用时播放层只输出音频。</summary>
    bool IsAvailable { get; }
}

/// <summary>
/// 通过 libmpv render API 自行绘制视频帧的输出表面。播放层在 <c>mpv_initialize</c> 之后
/// 调用 <see cref="AttachMpvContext"/>，在销毁 mpv 核心之前调用 <see cref="ReleaseMpvContextAsync"/>。
/// </summary>
public interface IMpvRenderVideoOutput : IVideoOutput
{
    /// <summary>是否使用 OpenGL 渲染后端（可启用硬件解码的帧互操作）。</summary>
    bool UsesOpenGl { get; }

    /// <summary>
    /// 挂接已初始化的 libmpv 上下文；必须在加载第一个文件之前返回可用的渲染上下文。
    /// 实现可能由后台线程调用，内部需要自行切回 UI 线程。
    /// </summary>
    void AttachMpvContext(IntPtr mpvContext);

    /// <summary>
    /// 在 mpv 核心销毁之前释放渲染上下文。返回 false 表示渲染上下文仍未释放，
    /// 此时调用方不能销毁 mpv 核心，否则属于未定义行为。
    /// </summary>
    Task<bool> ReleaseMpvContextAsync(CancellationToken cancellationToken = default);
}
