using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Models;
using AvaStudio.Services;
using AvaStudio.Services.Settings;

namespace AvaStudio.ViewModels.Pages;

/// <summary>
/// 设置页：管理音乐/视频资料库位置、扫描状态、主题/语言显示与应用数据目录。
/// 资料库位置的增删会立即写盘，并在成功时触发一次扫描。
/// </summary>
public sealed partial class SettingsPageViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<string> _reportStatus;

    public SettingsPageViewModel(AppServices services, Action<string> reportStatus)
        : base("设置", Symbol.Settings)
    {
        _services = services;
        _reportStatus = reportStatus;
        _services.Settings.Changed += OnSettingsChanged;
        _services.Library.Changed += OnLibraryChanged;
        _services.Library.ScanProgressChanged += OnScanProgressChanged;
        _services.Library.ScanStateChanged += OnScanStateChanged;
        SyncSettingValues();
        RebuildLocations();
    }

    /// <summary>主题选项；索引与应用设置中的 system/light/dark 一一对应。</summary>
    public IReadOnlyList<string> ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    /// <summary>当前主题索引。</summary>
    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    /// <summary>播放结束后是否自动播放下一项。</summary>
    [ObservableProperty]
    public partial bool AutoAdvance { get; set; }

    /// <summary>是否允许多个应用实例；修改后下次启动生效。</summary>
    [ObservableProperty]
    public partial bool MultiInstance { get; set; }

    /// <summary>是否记录最近播放；关闭后删除已有记录文件。</summary>
    [ObservableProperty]
    public partial bool RecordRecentPlayback { get; set; }

    /// <summary>是否使用 GPU 视频渲染；修改后下次启动生效。</summary>
    [ObservableProperty]
    public partial bool UseGpuVideoRendering { get; set; }

    /// <summary>应用版本号。</summary>
    public string AppVersion => typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>关于卡片的副标题：版本与资料库统计。</summary>
    public string AboutSummary => $"版本 {AppVersion} · {LibrarySummary}";

    /// <summary>音乐资料库位置。</summary>
    public ObservableCollection<LibraryLocationItemViewModel> MusicLocations { get; } = [];

    /// <summary>视频资料库位置。</summary>
    public ObservableCollection<LibraryLocationItemViewModel> VideoLocations { get; } = [];

    /// <summary>音乐位置区域是否展开。</summary>
    [ObservableProperty]
    public partial bool IsMusicExpanded { get; set; }

    /// <summary>视频位置区域是否展开。</summary>
    [ObservableProperty]
    public partial bool IsVideoExpanded { get; set; }

    /// <summary>是否有音乐位置。</summary>
    public bool HasMusicLocations => MusicLocations.Count > 0;

    /// <summary>是否有视频位置。</summary>
    public bool HasVideoLocations => VideoLocations.Count > 0;

    /// <summary>音乐位置数量。</summary>
    public int MusicLocationCount => MusicLocations.Count;

    /// <summary>视频位置数量。</summary>
    public int VideoLocationCount => VideoLocations.Count;

    /// <summary>音乐位置摘要。</summary>
    public string MusicLocationSummary => MusicLocationCount == 0
        ? "尚未添加位置"
        : $"指定了 {MusicLocationCount} 个位置";

    /// <summary>视频位置摘要。</summary>
    public string VideoLocationSummary => VideoLocationCount == 0
        ? "尚未添加位置"
        : $"指定了 {VideoLocationCount} 个位置";

    /// <summary>音乐区域折叠图标。</summary>
    public Symbol MusicChevron => IsMusicExpanded ? Symbol.ChevronUp : Symbol.ChevronDown;

    /// <summary>视频区域折叠图标。</summary>
    public Symbol VideoChevron => IsVideoExpanded ? Symbol.ChevronUp : Symbol.ChevronDown;

    /// <summary>语言显示文本。</summary>
    public string LanguageDisplayName => string.Equals(
        _services.Settings.Language,
        AppSettingsService.DefaultLanguage,
        StringComparison.OrdinalIgnoreCase)
        ? "简体中文"
        : _services.Settings.Language;

    /// <summary>应用数据目录。</summary>
    public string SettingsDirectory => _services.Settings.SettingsDirectory;

    /// <summary>资料库统计文本。</summary>
    public string LibrarySummary
    {
        get
        {
            var snapshot = _services.Library.Snapshot;
            return $"{snapshot.MusicItems.Count} 首歌曲 · {snapshot.Albums.Count} 张专辑 · {snapshot.VideoItems.Count} 个视频";
        }
    }

    /// <summary>扫描状态文本。</summary>
    public string ScanStatusText => _services.Library.ScanStatus;

    /// <summary>是否正在扫描。</summary>
    public bool IsScanning => _services.Library.IsScanning;

    /// <summary>扫描进度 0..1。</summary>
    [ObservableProperty]
    public partial double ScanProgress { get; set; }

    partial void OnIsMusicExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(MusicChevron));
    }

    partial void OnIsVideoExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(VideoChevron));
    }

    /// <summary>主题切换立即应用到应用级 ThemeVariant，并写入设置。</summary>
    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };

        _services.Settings.SetTheme(theme);
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    partial void OnAutoAdvanceChanged(bool value)
    {
        _services.Settings.SetAutoAdvance(value);
    }

    partial void OnMultiInstanceChanged(bool value)
    {
        _services.Settings.SetMultiInstance(value);
    }

    partial void OnRecordRecentPlaybackChanged(bool value)
    {
        _services.Settings.SetRecordRecentPlayback(value);
    }

    partial void OnUseGpuVideoRenderingChanged(bool value)
    {
        _services.Settings.SetUseGpuVideoRendering(value);
    }

    /// <summary>把设置文件中的值同步到界面属性，避免外部修改后控件显示旧值。</summary>
    private void SyncSettingValues()
    {
        if (AutoAdvance != _services.Settings.AutoAdvance)
        {
            AutoAdvance = _services.Settings.AutoAdvance;
        }

        if (MultiInstance != _services.Settings.MultiInstance)
        {
            MultiInstance = _services.Settings.MultiInstance;
        }

        if (RecordRecentPlayback != _services.Settings.RecordRecentPlayback)
        {
            RecordRecentPlayback = _services.Settings.RecordRecentPlayback;
        }

        if (UseGpuVideoRendering != _services.Settings.UseGpuVideoRendering)
        {
            UseGpuVideoRendering = _services.Settings.UseGpuVideoRendering;
        }

        var themeIndex = _services.Settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        if (SelectedThemeIndex != themeIndex)
        {
            SelectedThemeIndex = themeIndex;
        }
    }

    /// <summary>添加音乐资料库位置。</summary>
    [RelayCommand]
    private async Task AddMusicFolderAsync()
    {
        var folder = await _services.Desktop.PickFolderAsync("添加音乐资料库位置").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var result = _services.Settings.AddMusicFolder(folder);
        if (result == SettingsAddResult.Added)
        {
            IsMusicExpanded = true;
            await RefreshLibraryAsync().ConfigureAwait(true);
        }

        _reportStatus(DescribeAddResult(result, "音乐"));
    }

    /// <summary>添加视频资料库位置。</summary>
    [RelayCommand]
    private async Task AddVideoFolderAsync()
    {
        var folder = await _services.Desktop.PickFolderAsync("添加视频资料库位置").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var result = _services.Settings.AddVideoFolder(folder);
        if (result == SettingsAddResult.Added)
        {
            IsVideoExpanded = true;
            await RefreshLibraryAsync().ConfigureAwait(true);
        }

        _reportStatus(DescribeAddResult(result, "视频"));
    }

    /// <summary>重新扫描资料库。</summary>
    [RelayCommand]
    private async Task RefreshLibraryAsync()
    {
        _reportStatus("正在扫描资料库…");
        var result = await _services.Library.RefreshAsync().ConfigureAwait(true);
        _reportStatus(result.WasCancelled
            ? "资料库扫描已取消。"
            : result.Error is not null
                ? $"资料库扫描失败：{result.Error.Message}"
                : $"资料库已更新：{result.TotalItems} 个媒体文件。");
        RaiseLibraryProperties();
    }

    /// <summary>打开应用数据目录。</summary>
    [RelayCommand]
    private void OpenSettingsFolder()
    {
        _reportStatus(_services.Desktop.OpenDirectory(_services.Settings.SettingsDirectory)
            ? "已打开应用数据文件夹。"
            : "应用数据文件夹尚不存在。");
    }

    private void RemoveMusicLocation(string path)
    {
        if (_services.Settings.RemoveMusicFolder(path))
        {
            _reportStatus("已移除音乐资料库位置。");
            _ = RefreshLibraryAsync();
        }
        else
        {
            _reportStatus("无法移除该资料库位置。");
        }
    }

    private void RemoveVideoLocation(string path)
    {
        if (_services.Settings.RemoveVideoFolder(path))
        {
            _reportStatus("已移除视频资料库位置。");
            _ = RefreshLibraryAsync();
        }
        else
        {
            _reportStatus("无法移除该资料库位置。");
        }
    }

    private void RebuildLocations()
    {
        MusicLocations.Clear();
        foreach (var path in _services.Settings.MusicFolders)
        {
            MusicLocations.Add(new LibraryLocationItemViewModel(path, RemoveMusicLocation));
        }

        VideoLocations.Clear();
        foreach (var path in _services.Settings.VideoFolders)
        {
            VideoLocations.Add(new LibraryLocationItemViewModel(path, RemoveVideoLocation));
        }

        if (MusicLocations.Count == 0)
        {
            IsMusicExpanded = false;
        }

        if (VideoLocations.Count == 0)
        {
            IsVideoExpanded = false;
        }

        SyncSettingValues();
        RaiseLocationProperties();
    }

    private void RaiseLocationProperties()
    {
        OnPropertyChanged(nameof(HasMusicLocations));
        OnPropertyChanged(nameof(HasVideoLocations));
        OnPropertyChanged(nameof(MusicLocationCount));
        OnPropertyChanged(nameof(VideoLocationCount));
        OnPropertyChanged(nameof(MusicLocationSummary));
        OnPropertyChanged(nameof(VideoLocationSummary));
        OnPropertyChanged(nameof(LanguageDisplayName));
        OnPropertyChanged(nameof(AppVersion));
    }

    private void RaiseLibraryProperties()
    {
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(AboutSummary));
        OnPropertyChanged(nameof(ScanStatusText));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(ScanProgress));
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RebuildLocations();
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        RaiseLibraryProperties();
    }

    private void OnScanProgressChanged(object? sender, LibraryScanProgress progress)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnScanProgressChanged(sender, progress));
            return;
        }

        ScanProgress = progress.Fraction;
        RaiseLibraryProperties();
    }

    private void OnScanStateChanged(object? sender, EventArgs e)
    {
        if (!_services.Library.IsScanning)
        {
            ScanProgress = 0;
        }

        RaiseLibraryProperties();
    }

    private static string DescribeAddResult(SettingsAddResult result, string mediaKind) => result switch
    {
        SettingsAddResult.Added => $"已添加{mediaKind}资料库位置，正在扫描…",
        SettingsAddResult.Duplicate => $"该文件夹已在{mediaKind}资料库中。",
        SettingsAddResult.SaveFailed => "无法保存资料库位置，请检查本地设置目录。",
        _ => "无法使用该文件夹位置。",
    };
}

/// <summary>资料库位置列表项。</summary>
public sealed class LibraryLocationItemViewModel
{
    public LibraryLocationItemViewModel(string path, Action<string> remove)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        Name = string.IsNullOrWhiteSpace(name) ? path : name;
        RemoveCommand = new RelayCommand(() => remove(path));
    }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>完整路径。</summary>
    public string Path { get; }

    /// <summary>移除命令。</summary>
    public IRelayCommand RemoveCommand { get; }
}
