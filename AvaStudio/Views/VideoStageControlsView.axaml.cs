using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaStudio.ViewModels;

namespace AvaStudio.Views;

/// <summary>
/// 独立播放页的底部控制条，覆盖在视频/封面之上。
/// 进度滑杆与其它播放器共用同一套拖动规则：拖动冻结回写，松开只提交一次定位。
/// </summary>
public partial class VideoStageControlsView : UserControl
{
    private bool _isSeeking;
    private PlayerBarViewModel? _viewModel;

    /// <summary>是否正在拖动进度；供自动隐藏逻辑在拖动期间保持控制条可见。</summary>
    public bool IsSeeking => _isSeeking;

    public VideoStageControlsView()
    {
        InitializeComponent();

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
        }
    }

    /// <summary>实时位置只在非拖动状态下写入滑杆，拖动期间由用户输入决定。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.Position))
        {
            SyncPosition();
        }
    }

    private void SyncPosition()
    {
        if (_isSeeking || _viewModel is null)
        {
            return;
        }

        ProgressSlider.Value = _viewModel.Position;
    }

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

    private void OnProgressSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CommitSeek();
    }

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
}
