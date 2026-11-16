using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Models;
using AvaStudio.Playback;
using AvaStudio.Platform;
using AvaStudio.Services;
using AvaStudio.Services.Library;
using AvaStudio.ViewModels.Pages;
using AvaStudio.ViewModels.Services;

namespace AvaStudio.ViewModels;

/// <summary>
/// 应用根 ViewModel：持有页面实例、导航、全局状态栏、播放条和组合根服务。
/// 页面切换不重建页面实例；详情页按需创建，返回时回到对应列表页。
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly DesktopService _desktop;
    private readonly AppServices _services;
    private readonly HomePageViewModel _homePage;
    private readonly MusicPageViewModel _musicPage;
    private readonly VideosPageViewModel _videosPage;
    private readonly NetworkPageViewModel _networkPage;
    private readonly SettingsPageViewModel _settingsPage;
    private readonly SearchPageViewModel _searchPage;
    private readonly Stack<PageViewModelBase> _backStack = [];
    private bool _restoringNavigation;
    private IVideoOutput? _videoOutput;

    public MainViewModel()
    {
        _desktop = new DesktopService();
        _services = new AppServices(_desktop, () => _videoOutput);
        PlayerBar = new PlayerBarViewModel(_services);

        var playlistsPage = new PlaylistsPageViewModel(_services, message => ShowStatus(message));
        QueuePage = new QueuePageViewModel(_services, message => ShowStatus(message));
        _searchPage = new SearchPageViewModel(_services, OpenSettings);
        _settingsPage = new SettingsPageViewModel(_services, message => ShowStatus(message));
        _musicPage = new MusicPageViewModel(_services, OpenAlbum, OpenArtist, message => ShowStatus(message));
        _videosPage = new VideosPageViewModel(_services, message => ShowStatus(message));
        _networkPage = new NetworkPageViewModel(_desktop, message => ShowStatus(message));
        _homePage = new HomePageViewModel(_services, OpenFilesAsync, OpenFolderAsync);
        PlaylistsPage = playlistsPage;

        NavigationItems =
        [
            new NavigationItem("主页", Symbol.Home, _homePage),
            new NavigationItem("音乐", Symbol.MusicNote2, _musicPage),
            new NavigationItem("视频", Symbol.Video, _videosPage),
            new NavigationItem("网络", Symbol.Server, _networkPage),
            new NavigationItem("播放队列", Symbol.DocumentQueue, QueuePage, showSeparator: true),
            new NavigationItem("播放列表", Symbol.Collections, PlaylistsPage),
        ];

        SelectedPage = _homePage;
        SelectedNavigationItem = NavigationItems[0];

        PlayerBar.ImmersiveToggleRequested += (_, _) => ToggleImmersive();
        _services.Playback.StatusReported += message => ShowStatus(message);
        _services.Playback.CurrentItemChanged += OnPlaybackCurrentItemChanged;
        _services.Playback.StateChanged += OnPlaybackStateChanged;
        _services.Library.Changed += OnLibraryChanged;
        _ = InitializeAsync();
    }

    /// <summary>底部播放条状态。</summary>
    public PlayerBarViewModel PlayerBar { get; }

    /// <summary>播放队列页。</summary>
    public QueuePageViewModel QueuePage { get; }

    /// <summary>播放列表页。</summary>
    public PlaylistsPageViewModel PlaylistsPage { get; }

    /// <summary>主导航项。</summary>
    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    /// <summary>当前页面。</summary>
    [ObservableProperty]
    public partial PageViewModelBase SelectedPage { get; set; }

    /// <summary>当前选中的导航项；详情页/搜索页时为 null。</summary>
    [ObservableProperty]
    public partial NavigationItem? SelectedNavigationItem { get; set; }

    /// <summary>搜索框文本。</summary>
    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    /// <summary>状态栏是否可见。</summary>
    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    /// <summary>状态栏是否显示“查看播放队列”。</summary>
    [ObservableProperty]
    public partial bool IsStatusQueueActionVisible { get; set; }

    /// <summary>状态栏文本。</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>是否正在播放视频；用于显示视频原生表面。</summary>
    [ObservableProperty]
    public partial bool IsVideoStageVisible { get; set; }

    /// <summary>是否启用沉浸/全屏播放。</summary>
    [ObservableProperty]
    public partial bool IsImmersiveOpen { get; set; }

    /// <summary>沉浸播放是否为视频（视频时让原生表面铺满内容区）。</summary>
    public bool IsImmersiveVideo => IsImmersiveOpen && PlayerBar.IsVideo;

    /// <summary>沉浸播放是否为音频（音频时显示大封面覆盖层）。</summary>
    public bool IsImmersiveAudio => IsImmersiveOpen && !PlayerBar.IsVideo;

    /// <summary>窗口标题。</summary>
    public string WindowTitle => $"AvaStudio · {SelectedPage.Title}";

    /// <summary>是否可以返回上一页。</summary>
    public bool CanGoBack => _backStack.Count > 0;

    /// <summary>库外壳主题来自设置；只有媒体表面使用局部深色。</summary>
    public ThemeVariant AppThemeVariant => _services.Settings.Theme switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>设置页是否被选中。</summary>
    public bool IsSettingsSelected => ReferenceEquals(SelectedPage, _settingsPage);

    /// <summary>迷你播放条是否显示；设置页不保留常驻播放条。</summary>
    public bool IsPlayerBarVisible => !IsSettingsSelected;

    /// <summary>资料库统计文本，供状态栏/主页显示。</summary>
    public string LibrarySummary
    {
        get
        {
            var snapshot = _services.Library.Snapshot;
            return $"{snapshot.MusicItems.Count} 首歌曲 · {snapshot.VideoItems.Count} 个视频";
        }
    }

    partial void OnSelectedPageChanged(PageViewModelBase value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsPlayerBarVisible));

        // 视频表面只在播放页展开；退出播放页后允许迷你播放条继续，不再因换页停止播放。
    }

    partial void OnIsImmersiveOpenChanged(bool value)
    {
        PlayerBar.IsImmersive = value;
        OnPropertyChanged(nameof(IsImmersiveVideo));
        OnPropertyChanged(nameof(IsImmersiveAudio));
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        if (!ReferenceEquals(SelectedPage, value.Page))
        {
            if (!_restoringNavigation)
            {
                PushBack(SelectedPage);
            }

            SelectedPage = value.Page;
        }
    }

    /// <summary>把平台 TopLevel 交给文件选择器/系统外壳服务。</summary>
    public void AttachTopLevel(TopLevel? topLevel)
    {
        _desktop.Attach(topLevel);
    }

    /// <summary>是否使用 GPU 视频渲染；由设置页控制，视频表面创建时读取。</summary>
    public bool UseGpuVideoRendering => _services.Settings.UseGpuVideoRendering;

    /// <summary>由 MainView 在视频表面创建后调用。</summary>
    public void AttachVideoOutput(IVideoOutput videoOutput)
    {
        _videoOutput = videoOutput;
    }

    /// <summary>提交搜索框内容并打开搜索页。</summary>
    public void SubmitSearch()
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchPage.Query = string.Empty;
            ShowPage(_searchPage, trackHistory: true);
            return;
        }

        _searchPage.Query = query;
        SearchQuery = string.Empty;
        ShowPage(_searchPage, trackHistory: true);
    }

    /// <summary>接收窗口拖放路径，展开文件夹并加入队列。</summary>
    public Task AcceptDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        return QueuePage.AddPathsAsync(paths);
    }

    /// <summary>显示状态栏消息。</summary>
    public void ShowStatus(string message, bool canOpenQueue = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusMessage = message;
        IsStatusQueueActionVisible = canOpenQueue;
        IsStatusVisible = true;
    }

    /// <summary>打开文件选择器并加入队列。</summary>
    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        var files = await _desktop.PickMediaFilesAsync().ConfigureAwait(true);
        if (files.Count == 0)
        {
            return;
        }

        await QueuePage.AddPathsAsync(files).ConfigureAwait(true);
    }

    /// <summary>打开文件夹选择器并递归加入队列。</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var folder = await _desktop.PickFolderAsync("打开媒体文件夹").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var files = await Task.Run(() => DesktopService.EnumerateMediaFiles(folder)).ConfigureAwait(true);
        if (files.Count == 0)
        {
            ShowStatus("所选文件夹中没有可用的媒体文件。");
            return;
        }

        await QueuePage.AddPathsAsync(files).ConfigureAwait(true);
    }

    /// <summary>打开搜索页。</summary>
    [RelayCommand]
    private void OpenSearch()
    {
        ShowPage(_searchPage, trackHistory: true);
    }

    /// <summary>打开设置页。</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        ShowPage(_settingsPage, trackHistory: true);
    }

    /// <summary>返回上一页；标题栏后退按钮和 Alt+Left 使用同一命令。</summary>
    [RelayCommand(CanExecute = nameof(CanGoBackNow))]
    private void GoBack()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        var page = _backStack.Pop();
        var navigationItem = NavigationItems.FirstOrDefault(item => ReferenceEquals(item.Page, page));
        _restoringNavigation = true;
        try
        {
            SelectedNavigationItem = navigationItem;
        }
        finally
        {
            _restoringNavigation = false;
        }

        SelectedPage = page;
        RaiseBackState();
    }

    private bool CanGoBackNow() => CanGoBack;

    /// <summary>跳转到播放队列。</summary>
    [RelayCommand]
    private void GoToQueue()
    {
        SelectedNavigationItem = NavigationItems.First(item => ReferenceEquals(item.Page, QueuePage));
        IsStatusVisible = false;
    }

    /// <summary>返回主页。</summary>
    [RelayCommand]
    private void NavigateHome()
    {
        ClearBackStack();
        SelectedNavigationItem = NavigationItems[0];
        ShowPage(_homePage, clearNavigationSelection: false);
    }

    /// <summary>关闭状态栏。</summary>
    [RelayCommand]
    private void DismissStatus()
    {
        IsStatusVisible = false;
    }

    /// <summary>打开专辑详情页。</summary>
    private void OpenAlbum(AlbumGroup album)
    {
        var page = new AlbumDetailPageViewModel(_services, album, ShowMusicPage);
        ShowPage(page, trackHistory: true);
    }

    /// <summary>打开艺术家详情页。</summary>
    private void OpenArtist(ArtistGroup artist)
    {
        var page = new ArtistDetailPageViewModel(_services, artist, ShowMusicPage, OpenAlbum);
        ShowPage(page, trackHistory: true);
    }

    /// <summary>切换页面；trackHistory 为 true 时记录上一页供标题栏后退。</summary>
    private void ShowPage(
        PageViewModelBase page,
        bool clearNavigationSelection = true,
        bool trackHistory = false)
    {
        if (clearNavigationSelection)
        {
            SelectedNavigationItem = null;
        }

        if (trackHistory && !ReferenceEquals(SelectedPage, page))
        {
            PushBack(SelectedPage);
        }

        SelectedPage = page;
    }

    /// <summary>记录上一页；连续打开同一页时不重复入栈。</summary>
    private void PushBack(PageViewModelBase page)
    {
        if (_backStack.Count > 0 && ReferenceEquals(_backStack.Peek(), page))
        {
            return;
        }

        _backStack.Push(page);
        RaiseBackState();
    }

    private void ShowMusicPage()
    {
        ClearBackStack();
        SelectedNavigationItem = NavigationItems[1];
        ShowPage(_musicPage, clearNavigationSelection: false);
    }

    /// <summary>清空返回栈并刷新标题栏后退按钮状态。</summary>
    private void ClearBackStack()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        _backStack.Clear();
        RaiseBackState();
    }

    private void RaiseBackState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _services.InitializeAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(LibrarySummary));
        }
        catch (Exception exception)
        {
            ShowStatus($"初始化资料库失败：{exception.Message}");
        }
    }

    private void OnPlaybackCurrentItemChanged(object? sender, EventArgs e)
    {
        UpdateVideoStageVisibility();
        OnPropertyChanged(nameof(IsImmersiveVideo));
        OnPropertyChanged(nameof(IsImmersiveAudio));
        if (PlayerBar.HasMedia == false)
        {
            IsImmersiveOpen = false;
        }
    }

    /// <summary>
    /// 从资源管理器/命令行打开外部媒体。只有外部入口才自动进入播放页；
    /// 资料库内点击媒体仍走普通播放路径，先显示迷你播放条。
    /// </summary>
    public async Task OpenExternalMediaAsync(string path, bool enterImmersive = true)
    {
        if (!MediaFileTypes.IsSupported(path))
        {
            ShowStatus("不支持该文件类型。");
            return;
        }

        try
        {
            var item = await _services.Library.LoadItemAsync(path).ConfigureAwait(true);
            await _services.Playback.PlayAsync(item, [item]).ConfigureAwait(true);

            if (enterImmersive)
            {
                IsImmersiveOpen = true;
            }
        }
        catch (Exception exception)
        {
            ShowStatus($"无法打开媒体：{exception.Message}");
        }
    }

    /// <summary>打开/关闭沉浸播放；没有媒体时忽略。</summary>
    public void ToggleImmersive()
    {
        if (!PlayerBar.HasMedia)
        {
            return;
        }

        IsImmersiveOpen = !IsImmersiveOpen;
    }

    /// <summary>进入独立播放页；没有媒体时忽略，已进入时保持不变。</summary>
    public void EnterImmersive()
    {
        if (!PlayerBar.HasMedia)
        {
            return;
        }

        IsImmersiveOpen = true;
    }

    /// <summary>关闭沉浸播放。</summary>
    public void CloseImmersive()
    {
        IsImmersiveOpen = false;
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        UpdateVideoStageVisibility();

        // 视频结束/停止后没有可显示的画面，自动退出播放页。
        if (_services.Playback.Snapshot.Phase is PlaybackPhase.Ended
            or PlaybackPhase.Stopped
            or PlaybackPhase.Failed)
        {
            if (IsImmersiveOpen && PlayerBar.IsVideo)
            {
                IsImmersiveOpen = false;
            }
        }
    }

    /// <summary>视频只在打开/播放/暂停阶段占用内容区；停止或结束后释放画面。</summary>
    private void UpdateVideoStageVisibility()
    {
        var playback = _services.Playback;
        IsVideoStageVisible = playback.CurrentItem?.Kind == MediaItemKind.Video
            && playback.Snapshot.Phase is PlaybackPhase.Opening
                or PlaybackPhase.Playing
                or PlaybackPhase.Paused;
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LibrarySummary));
    }

    /// <summary>应用退出时释放播放会话。</summary>
    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

