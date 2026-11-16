using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Helpers;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Services;
using AvaStudio.Services.Library;
using AvaStudio.Services.Playback;
using AvaStudio.Services.Settings;

namespace AvaStudio.ViewModels;

/// <summary>
/// 底部播放条与沉浸播放器的共享状态。它把 <see cref="PlaybackCoordinator"/>
/// 的队列/引擎快照转换为可绑定属性，并把界面命令转回协调层。
/// </summary>
public sealed partial class PlayerBarViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private bool _updatingFromPlayback;
    private MediaItem? _loadedArtworkItem;

    /// <summary>请求切换沉浸播放器；由根 ViewModel 处理窗口/布局状态。</summary>
    public event EventHandler? ImmersiveToggleRequested;

    /// <summary>
    /// 请求独立播放页的系统全屏；未进入播放页时由 MainView 先打开播放页。
    /// </summary>
    public event EventHandler? FullscreenToggleRequested;

    public PlayerBarViewModel(AppServices services)
    {
        _services = services;
        _services.Playback.StateChanged += OnPlaybackStateChanged;
        _services.Playback.CurrentItemChanged += OnCurrentItemChanged;
        Refresh();
    }

    /// <summary>当前是否有媒体。</summary>
    [ObservableProperty]
    public partial bool HasMedia { get; set; }

    /// <summary>是否正在播放。</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>是否正在打开文件。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>当前标题。</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>艺术家与专辑副标题。</summary>
    [ObservableProperty]
    public partial string MetaText { get; set; } = string.Empty;

    /// <summary>当前播放位置（秒）。</summary>
    [ObservableProperty]
    public partial double Position { get; set; }

    /// <summary>当前媒体总时长（秒）。</summary>
    [ObservableProperty]
    public partial double Duration { get; set; }

    /// <summary>音量 0..100。</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = AppSettingsService.DefaultVolume;

    /// <summary>是否静音。</summary>
    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>是否随机播放。</summary>
    [ObservableProperty]
    public partial bool Shuffle { get; set; }

    /// <summary>循环模式。</summary>
    [ObservableProperty]
    public partial RepeatMode Repeat { get; set; }

    /// <summary>当前封面。</summary>
    [ObservableProperty]
    public partial Bitmap? Artwork { get; set; }

    /// <summary>是否处于沉浸播放模式；由根 ViewModel 同步。</summary>
    [ObservableProperty]
    public partial bool IsImmersive { get; set; }

    /// <summary>独立播放页是否处于系统全屏；由 MainView 同步。</summary>
    [ObservableProperty]
    public partial bool IsFullscreen { get; set; }

    /// <summary>全屏按钮提示。</summary>
    public string FullscreenTip => IsFullscreen ? "退出全屏" : "全屏";

    /// <summary>是否显示封面。</summary>
    public bool HasArtwork => Artwork is not null;

    /// <summary>当前位置文本。</summary>
    public string PositionText => DurationFormatter.FormatLong(TimeSpan.FromSeconds(Math.Max(0, Position)));

    /// <summary>总时长文本；未知时显示 --:--。</summary>
    public string DurationText => Duration > 0
        ? DurationFormatter.Format(TimeSpan.FromSeconds(Duration))
        : "--:--";

    /// <summary>播放/暂停按钮图标。</summary>
    public Symbol PlayPauseSymbol => IsPlaying ? Symbol.Pause : Symbol.Play;

    /// <summary>播放/暂停按钮提示。</summary>
    public string PlayPauseTip => IsPlaying ? "暂停" : "播放";

    /// <summary>循环按钮图标。</summary>
    public Symbol RepeatSymbol => Repeat switch
    {
        RepeatMode.One => Symbol.ArrowRepeat1,
        RepeatMode.All => Symbol.ArrowRepeatAll,
        _ => Symbol.ArrowRepeatAllOff,
    };

    /// <summary>循环按钮提示。</summary>
    public string RepeatTip => Repeat switch
    {
        RepeatMode.One => "单曲循环",
        RepeatMode.All => "列表循环",
        _ => "不循环",
    };

    /// <summary>音量按钮图标。</summary>
    public Symbol VolumeSymbol => IsMuted || Volume <= 0
        ? Symbol.SpeakerMute
        : Volume < 34 ? Symbol.Speaker1 : Symbol.Speaker2;

    /// <summary>当前媒体是否为视频。</summary>
    public bool IsVideo => _services.Playback.CurrentItem?.Kind == MediaItemKind.Video;

    /// <summary>播放或暂停。</summary>
    [RelayCommand]
    private Task TogglePlayPauseAsync() => _services.Playback.TogglePlayPauseAsync();

    /// <summary>下一首。</summary>
    [RelayCommand]
    private Task NextAsync() => _services.Playback.NextAsync();

    /// <summary>上一首。</summary>
    [RelayCommand]
    private Task PreviousAsync() => _services.Playback.PreviousAsync();

    /// <summary>停止播放。</summary>
    [RelayCommand]
    private Task StopAsync() => _services.Playback.StopAsync();

    /// <summary>跳转到指定秒数。</summary>
    [RelayCommand]
    private Task SeekToAsync(double seconds) =>
        _services.Playback.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    /// <summary>切换静音。</summary>
    [RelayCommand]
    private Task ToggleMuteAsync() => _services.Playback.ToggleMuteAsync();

    /// <summary>切换随机播放。</summary>
    [RelayCommand]
    private void ToggleShuffle() => _services.Playback.ToggleShuffle();

    /// <summary>切换循环模式。</summary>
    [RelayCommand]
    private void CycleRepeatMode() => _services.Playback.CycleRepeatMode();

    /// <summary>请求打开/关闭沉浸播放器。</summary>
    [RelayCommand]
    private void ToggleImmersive() => ImmersiveToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>请求切换独立播放页的系统全屏；迷你播放条会先由 MainView 打开播放页。</summary>
    [RelayCommand]
    private void ToggleFullscreen() => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

    partial void OnIsFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(FullscreenTip));
    }

    /// <summary>刷新全部绑定属性。</summary>
    private void Refresh()
    {
        _updatingFromPlayback = true;
        try
        {
            var playback = _services.Playback;
            var item = playback.CurrentItem;
            var snapshot = playback.Snapshot;

            HasMedia = item is not null;
            Title = item?.DisplayTitle ?? string.Empty;
            MetaText = item is null
                ? string.Empty
                : item.Kind == MediaItemKind.Video
                    ? GetVideoFolderLabel(item.Path)
                    : item.DisplayMeta;
            IsPlaying = snapshot.Phase == PlaybackPhase.Playing;
            IsLoading = snapshot.Phase is PlaybackPhase.Opening;
            Duration = snapshot.Duration?.TotalSeconds ?? item?.Duration.TotalSeconds ?? 0;
            Position = Math.Clamp(snapshot.Position.TotalSeconds, 0, Duration > 0 ? Duration : double.MaxValue);
            Volume = playback.Volume;
            IsMuted = playback.IsMuted;
            Shuffle = playback.IsShuffle;
            Repeat = playback.RepeatMode;
            OnPropertyChanged(nameof(IsVideo));

            if (item is not null && !ReferenceEquals(item, _loadedArtworkItem))
            {
                _loadedArtworkItem = item;
                _ = LoadArtworkAsync(item);
            }
            else if (item is null)
            {
                _loadedArtworkItem = null;
                Artwork = null;
            }
        }
        finally
        {
            _updatingFromPlayback = false;
        }

        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlayPauseSymbol));
        OnPropertyChanged(nameof(PlayPauseTip));
        OnPropertyChanged(nameof(RepeatSymbol));
        OnPropertyChanged(nameof(RepeatTip));
        OnPropertyChanged(nameof(VolumeSymbol));
        OnPropertyChanged(nameof(HasArtwork));
    }

    /// <summary>视频副标题只显示所在文件夹名，避免把完整路径暴露在界面上。</summary>
    private static string GetVideoFolderLabel(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        var folderName = string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : System.IO.Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(folderName) ? "视频" : folderName;
    }

    /// <summary>加载当前媒体封面。</summary>
    private async Task LoadArtworkAsync(MediaItem item)
    {
        var bitmap = await _services.ArtworkLoader
            .LoadAsync(item.ArtworkPath, ArtworkLoader.PlayerWidth)
            .ConfigureAwait(true);
        if (ReferenceEquals(_loadedArtworkItem, item))
        {
            Artwork = bitmap;
        }
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void OnCurrentItemChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
    }

    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseSymbol));
        OnPropertyChanged(nameof(PlayPauseTip));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(VolumeSymbol));
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeSymbol));
        if (!_updatingFromPlayback)
        {
            _ = _services.Playback.SetVolumeAsync(value);
        }
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatSymbol));
        OnPropertyChanged(nameof(RepeatTip));
    }

    partial void OnShuffleChanged(bool value)
    {
    }

    partial void OnArtworkChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasArtwork));
    }
}
