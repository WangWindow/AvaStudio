using System;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaStudio.ViewModels;

namespace AvaStudio.Views;

/// <summary>
/// 底部迷你播放条。进度滑杆的拖动与实时位置更新相互独立：
/// 非拖动状态才把 <see cref="PlayerBarViewModel.Position"/> 写入滑杆，
/// 松开时只把目标秒数交给 <see cref="PlayerBarViewModel.SeekToCommand"/> 一次。
/// </summary>
public partial class PlayerBarView : UserControl
{
    /// <summary>低于此宽度时折叠音量滑杆，只保留静音按钮。</summary>
    private const double VolumeSliderMinWidth = 620;

    private bool _isSeeking;
    private PlayerBarViewModel? _viewModel;

    public PlayerBarView()
    {
        InitializeComponent();

        // Slider 把手势处理在模板内部的 Track 上，因此用隧道阶段标记拖动开始；
        // 冒泡阶段仍能收到已处理事件，在滑杆更新完数值后提交定位。
        ProgressSlider.AddHandler(
            PointerPressedEvent,
            OnProgressSliderPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        ProgressSlider.AddHandler(
            PointerReleasedEvent,
            OnProgressSliderPointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        ProgressSlider.AddHandler(
            PointerCaptureLostEvent,
            OnProgressSliderPointerCaptureLost,
            RoutingStrategies.Direct | RoutingStrategies.Bubble,
            handledEventsToo: true);

        ProgressSlider.ValueChanged += OnProgressSliderValueChanged;
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnPlayerBarSizeChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as PlayerBarViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncPosition();
            UpdateImmersiveLabels();
        }
    }

    /// <summary>实时位置只在非拖动状态下写入滑杆，拖动期间由用户输入决定。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.Position))
        {
            SyncPosition();
        }
        else if (e.PropertyName == nameof(PlayerBarViewModel.IsImmersive))
        {
            UpdateImmersiveLabels();
        }
    }

    /// <summary>展开按钮和全屏按钮的提示随沉浸状态切换。</summary>
    private void UpdateImmersiveLabels()
    {
        var isImmersive = _viewModel?.IsImmersive == true;

        var expandLabel = isImmersive ? "收起播放器" : "展开播放器";
        AutomationProperties.SetName(ExpandButton, expandLabel);
        ToolTip.SetTip(ExpandButton, expandLabel);

        var fullscreenLabel = isImmersive ? "退出全屏" : "全屏";
        AutomationProperties.SetName(FullscreenButton, fullscreenLabel);
        ToolTip.SetTip(FullscreenButton, fullscreenLabel);
    }

    private void SyncPosition()
    {
        if (_isSeeking || _viewModel is null)
        {
            return;
        }

        ProgressSlider.Value = _viewModel.Position;
    }

    /// <summary>拖动开始：冻结位置回写，并显示目标时间提示。</summary>
    private void OnProgressSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ProgressSlider.IsEnabled ||
            !e.GetCurrentPoint(ProgressSlider).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isSeeking = true;
        UpdateSeekTip(ProgressSlider.Value);
        ToolTip.SetIsOpen(ProgressSlider, true);
    }

    /// <summary>松开：提交一次跳转，之后恢复跟随实时位置。</summary>
    private void OnProgressSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CommitSeek();
    }

    /// <summary>指针捕获意外丢失时也算结束拖动，避免残留冻结状态。</summary>
    private void OnProgressSliderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CommitSeek();
    }

    private void OnProgressSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSeeking)
        {
            UpdateSeekTip(e.NewValue);
        }
    }

    private void CommitSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        ToolTip.SetIsOpen(ProgressSlider, false);
        ToolTip.SetTip(ProgressSlider, null!);

        if (_viewModel is not null)
        {
            _viewModel.SeekToCommand.Execute(ProgressSlider.Value);
        }
    }

    private void UpdateSeekTip(double seconds)
    {
        ToolTip.SetTip(ProgressSlider, $"跳转到 {FormatSeekPosition(seconds)}");
    }

    private static string FormatSeekPosition(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private void OnPlayerBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        VolumeSlider.IsVisible = e.NewSize.Width >= VolumeSliderMinWidth;
    }
}
