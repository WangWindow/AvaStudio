using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaStudio.Platform;
using AvaStudio.ViewModels;
using AvaStudio.ViewModels.Pages;
using AvaStudio.Views.Pages;

namespace AvaStudio.Views;

public partial class MainView : UserControl
{
    private const double ExpandedThreshold = 1058;
    private const double CompactThreshold = 701;
    private const string MaximizeGeometry = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGeometry = "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    private readonly Dictionary<PageViewModelBase, Control> _pageViews = new();
    private string _layoutMode = "expanded";
    private bool _isNavigationOverlayOpen;
    private bool _isPaneCollapsed;
    private bool _prewarmed;
    private int _immersiveLayerToken;
    private bool? _immersiveApplied;
    private bool _immersiveFullScreen;
    private WindowState _restoreWindowState = WindowState.Normal;
    private MainViewModel? _viewModel;
    private Window? _window;
    private IVideoOutput? _videoOutput;
    private bool? _videoStageApplied;

    public MainView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnMainViewKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        NavigationList.ContainerPrepared += OnNavigationListContainerPrepared;
        NavigationList.AddHandler(
            PointerReleasedEvent,
            OnNavigationListPointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachToWindow(TopLevel.GetTopLevel(this) as Window);

        // 后台预热主导航页面视图，避免首次点击时同步构建整页内容。
        Dispatcher.UIThread.Post(PrewarmPageViews, DispatcherPriority.Background);
    }

    private void OnNavigationListContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ListBoxItem item && item.DataContext is NavigationItem navigationItem)
        {
            AutomationProperties.SetName(item, navigationItem.Label);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel?.AttachTopLevel(TopLevel.GetTopLevel(this));
        ApplyLayoutMode(Bounds.Width);

        AttachToWindow(TopLevel.GetTopLevel(this) as Window);
    }

    private void AttachToWindow(Window? window)
    {
        if (window is null)
        {
            DisposeVideoOutput();

            if (_window is null)
            {
                MainRoot.Classes.Set("desktop", false);
                MainRoot.Classes.Set("mica", false);
                MainRoot.Classes.Set("window-inactive", false);
            }

            return;
        }

        if (ReferenceEquals(_window, window))
        {
            return;
        }

        if (_window is not null)
        {
            DetachWindowEvents();
        }

        _window = window;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;
        MainRoot.Classes.Set("desktop", true);
        MinimizeButton.IsVisible = true;
        MaximizeButton.IsVisible = true;
        CloseButton.IsVisible = true;
        TitleBarDragRegion.IsVisible = true;
        UpdateWindowState();
        UpdateWindowMaterialClasses();

        // 原生视频表面必须在窗口存在后才创建，HWND/XID 才能固定下来。
        EnsureVideoOutput();
        ApplyImmersiveState(_viewModel?.IsImmersiveOpen == true, force: true);
        UpdateVideoStage();
        UpdateImmersiveAudioLayer(immediate: true);
    }

    private void DetachWindowEvents()
    {
        if (_window is null)
        {
            return;
        }

        _window.PropertyChanged -= OnWindowPropertyChanged;
        _window.Activated -= OnWindowActivated;
        _window.Deactivated -= OnWindowDeactivated;
    }

    /// <summary>
    /// 创建平台视频表面并放进 <c>VideoHost</c>。只在窗口宿主上创建一次；
    /// 表面缩到 1×1 但不会离开可视树，因此原生句柄可以复用。
    /// </summary>
    private void EnsureVideoOutput()
    {
        if (_videoOutput is not null)
        {
            return;
        }

        var output = VideoOutputFactory.Create();
        _videoOutput = output;
        output.Surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        output.Surface.VerticalAlignment = VerticalAlignment.Stretch;
        VideoHost.Content = output.Surface;
        _viewModel?.AttachVideoOutput(output);
    }

    /// <summary>窗口关闭/视图移除时释放原生表面；重新挂载时会重新创建。</summary>
    private void DisposeVideoOutput()
    {
        if (_videoStageApplied is not null)
        {
            _videoStageApplied = null;
            ApplyVideoStage(false);
        }

        if (_videoOutput is null)
        {
            return;
        }

        var output = _videoOutput;
        _videoOutput = null;
        output.Dispose();
        if (ReferenceEquals(VideoHost.Content, output.Surface))
        {
            VideoHost.Content = null;
        }
    }

    /// <summary>视频表面只在自己的播放页展开；库页面保持 1×1 隐藏。</summary>
    private void UpdateVideoStage()
    {
        ApplyVideoStage(_viewModel?.IsVideoStageVisible == true && _viewModel?.IsImmersiveOpen == true);
    }

    /// <summary>
    /// 视频播放页展开时把表面铺满内容区；否则缩成右下角 1×1。
    /// 无论哪种状态都保持 IsVisible=true，避免销毁原生窗口。
    /// </summary>
    private void ApplyVideoStage(bool isVisible)
    {
        if (_videoStageApplied == isVisible)
        {
            return;
        }

        _videoStageApplied = isVisible;

        if (isVisible)
        {
            VideoHost.Width = double.NaN;
            VideoHost.Height = double.NaN;
            VideoHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            VideoHost.VerticalAlignment = VerticalAlignment.Stretch;
            VideoHost.IsHitTestVisible = true;
        }
        else
        {
            VideoHost.Width = 1;
            VideoHost.Height = 1;
            VideoHost.HorizontalAlignment = HorizontalAlignment.Right;
            VideoHost.VerticalAlignment = VerticalAlignment.Bottom;
            VideoHost.IsHitTestVisible = false;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachWindowEvents();
        DisposeVideoOutput();
        ResetImmersiveVisuals();
        _window = null;
        MinimizeButton.IsVisible = false;
        MaximizeButton.IsVisible = false;
        CloseButton.IsVisible = false;
        TitleBarDragRegion.IsVisible = false;
        MainRoot.Classes.Set("desktop", false);
        MainRoot.Classes.Set("mica", false);

        _viewModel?.AttachTopLevel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdateWindowState();
        }
        else if (e.Property.Name == nameof(Window.ActualTransparencyLevel))
        {
            UpdateWindowMaterialClasses();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        MainRoot.Classes.Set("window-inactive", false);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        MainRoot.Classes.Set("window-inactive", true);
    }

    private void UpdateWindowMaterialClasses()
    {
        var isMica = _window?.ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        MainRoot.Classes.Set("mica", isMica);
        MainRoot.Classes.Set("window-inactive", _window?.IsActive == false);
    }

    private void UpdateWindowState()
    {
        var isMaximized = _window?.WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(isMaximized ? RestoreGeometry : MaximizeGeometry);

        var label = isMaximized ? "还原" : "最大化";
        ToolTip.SetTip(MaximizeButton, label);
        AutomationProperties.SetName(MaximizeButton, label);
        UpdateMaximizedContentInset();
    }

    /// <summary>
    /// 无边框窗口补回 WS_THICKFRAME 后，Windows 最大化矩形会比显示器工作区
    /// 每边多出 resize border，导致四边内容被裁切。这里按客户端尺寸与工作区
    /// 的实际差值给根布局加等量内边距；正常/全屏状态自动归零。
    /// </summary>
    private void UpdateMaximizedContentInset()
    {
        if (_window is null || _window.WindowState != WindowState.Maximized)
        {
            MainRoot.Margin = default;
            return;
        }

        var screen = _window.Screens.ScreenFromWindow(_window);
        if (screen is null)
        {
            MainRoot.Margin = default;
            return;
        }

        var scaling = _window.RenderScaling;
        if (scaling <= 0)
        {
            scaling = 1;
        }

        // Bounds 是 DIP，WorkingArea 是物理像素；先换算成物理差值再折回 DIP。
        var clientWidth = _window.Bounds.Width * scaling;
        var clientHeight = _window.Bounds.Height * scaling;
        var insetX = Math.Max(0, (clientWidth - screen.WorkingArea.Width) / 2) / scaling;
        var insetY = Math.Max(0, (clientHeight - screen.WorkingArea.Height) / 2) / scaling;

        MainRoot.Margin = new Thickness(insetX, insetY, insetX, insetY);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (HasInteractiveAncestor(e.Source as Visual))
        {
            return;
        }

        ToggleMaximize();
        e.Handled = true;
    }

    private void ToggleMaximize()
    {
        if (_window is null)
        {
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static bool HasInteractiveAncestor(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _window?.Close();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.AttachTopLevel(TopLevel.GetTopLevel(this));
            if (_videoOutput is not null)
            {
                _viewModel.AttachVideoOutput(_videoOutput);
            }

            ShowPageView(_viewModel.SelectedPage);
            ApplyImmersiveState(_viewModel.IsImmersiveOpen);
            UpdateVideoStage();
            UpdateImmersiveAudioLayer(immediate: true);
            Dispatcher.UIThread.Post(PrewarmPageViews, DispatcherPriority.Background);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPage))
        {
            SetNavigationOverlay(false);
            ShowPageView(_viewModel?.SelectedPage);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsVideoStageVisible))
        {
            UpdateVideoStage();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsImmersiveOpen))
        {
            ApplyImmersiveState(_viewModel?.IsImmersiveOpen == true);
            UpdateImmersiveAudioLayer();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsImmersiveAudio))
        {
            UpdateImmersiveAudioLayer();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsImmersiveVideo))
        {
            // 队列切到视频时重新套用标题栏/全屏/底栏状态，保持音频沉浸路径不变。
            ApplyImmersiveState(_viewModel?.IsImmersiveOpen == true, force: true);
            UpdateImmersiveAudioLayer();
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyLayoutMode(e.NewSize.Width);
        UpdateMaximizedContentInset();
    }

    /// <summary>
    /// 切换页面只改缓存视图的可见性，不重建页面、不重建 ViewModel。
    /// 新页面做 160ms 的淡入 + 8px 上移动画（如平台支持过渡）。
    /// </summary>
    private void ShowPageView(PageViewModelBase? page)
    {
        if (page is null)
        {
            return;
        }

        var view = EnsurePageView(page);
        if (view is null)
        {
            return;
        }

        foreach (var child in PageLayer.Children)
        {
            if (!ReferenceEquals(child, view))
            {
                child.IsVisible = false;
            }
        }

        if (view.IsVisible)
        {
            return;
        }

        view.IsVisible = true;
        view.Opacity = 0;
        view.RenderTransform = TransformOperations.Parse("translateY(8px)");

        // 等一帧再切到目标值，让透明度/位移过渡真正可见。
        DispatcherTimer.RunOnce(
            () =>
            {
                if (!view.IsVisible)
                {
                    return;
                }

                view.Opacity = 1;
                view.RenderTransform = TransformOperations.Parse("translateY(0px)");
            },
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render);
    }

    /// <summary>按需创建页面视图并缓存；详情页同样缓存，返回列表再进入不重建。</summary>
    private Control? EnsurePageView(PageViewModelBase page)
    {
        if (_pageViews.TryGetValue(page, out var cached))
        {
            return cached;
        }

        var view = CreatePageView(page);
        if (view is null)
        {
            return null;
        }

        view.DataContext = page;
        view.Classes.Add("page-view");
        view.IsVisible = false;
        PageLayer.Children.Add(view);
        _pageViews[page] = view;
        return view;
    }

    private static Control? CreatePageView(PageViewModelBase page) => page switch
    {
        HomePageViewModel => new HomePageView(),
        MusicPageViewModel => new MusicPageView(),
        VideosPageViewModel => new VideosPageView(),
        NetworkPageViewModel => new NetworkPageView(),
        QueuePageViewModel => new QueuePageView(),
        PlaylistsPageViewModel => new PlaylistsPageView(),
        SearchPageViewModel => new SearchPageView(),
        SettingsPageViewModel => new SettingsPageView(),
        AlbumDetailPageViewModel => new AlbumDetailPageView(),
        ArtistDetailPageViewModel => new ArtistDetailPageView(),
        _ => null,
    };

    /// <summary>后台预热主导航页面视图（隐藏状态），首次点击只做显示和布局。</summary>
    private void PrewarmPageViews()
    {
        if (_prewarmed || _viewModel is null)
        {
            return;
        }

        _prewarmed = true;
        foreach (var item in _viewModel.NavigationItems)
        {
            EnsurePageView(item.Page);
        }
    }

    /// <summary>
    /// 音频沉浸：隐藏标题栏并进入系统全屏；视频播放页保留标题栏、窗口边框和
    /// 可拖动的桌面窗口。退出时恢复进入全屏前的窗口状态。
    /// </summary>
    private void ApplyImmersiveState(bool isImmersive, bool force = false)
    {
        if (!force && _immersiveApplied == isImmersive)
        {
            return;
        }

        var isImmersiveAudio = isImmersive && _viewModel?.IsImmersiveAudio == true;
        var isImmersiveVideo = isImmersive && _viewModel?.IsImmersiveVideo == true;

        _immersiveApplied = isImmersive;
        MainRoot.Classes.Set("immersive", isImmersive);
        MainRoot.Classes.Set("video-immersive", isImmersiveVideo);

        // 视频沉浸保留 48 DIP 标题栏和窗口边框，方便拖动、Snap Layouts 和退出；
        // 音频沉浸才进入无标题栏的系统全屏。
        TitleBarStrip.IsVisible = !isImmersiveAudio;
        ShellGrid.RowDefinitions[0].Height = new GridLength(isImmersiveAudio ? 0 : 48);

        // 两种沉浸都暂时隐藏底部状态栏与播放条：视频不使用 Avalonia 内容覆盖原生画面。
        BottomHost.IsVisible = !isImmersive;

        if (isImmersive)
        {
            SetNavigationOverlay(false);

            if (isImmersiveAudio)
            {
                if (_window is not null && _window.WindowState != WindowState.FullScreen)
                {
                    _restoreWindowState = _window.WindowState == WindowState.Maximized
                        ? WindowState.Maximized
                        : WindowState.Normal;
                    _immersiveFullScreen = true;
                    _window.WindowState = WindowState.FullScreen;
                }
            }
            else if (_immersiveFullScreen && _window is not null)
            {
                // 音频沉浸切到视频时退出系统全屏，保留可拖动的标题栏。
                _window.WindowState = _restoreWindowState;
                _immersiveFullScreen = false;
            }
        }
        else if (_immersiveFullScreen && _window is not null)
        {
            _window.WindowState = _restoreWindowState;
            _immersiveFullScreen = false;
        }

        UpdateVideoStage();
    }

    private void ResetImmersiveVisuals()
    {
        _immersiveApplied = null;
        _immersiveFullScreen = false;
        _immersiveLayerToken++;
        ImmersiveLayer.IsVisible = false;
        ImmersiveLayer.Opacity = 0;
        MainRoot.Classes.Set("immersive", false);
        MainRoot.Classes.Set("video-immersive", false);
        TitleBarStrip.IsVisible = true;
        ShellGrid.RowDefinitions[0].Height = new GridLength(48);
        BottomHost.IsVisible = true;
    }

    /// <summary>音频沉浸层淡入/缩放；关闭时先淡出再折叠，避免硬切。</summary>
    private void UpdateImmersiveAudioLayer(bool immediate = false)
    {
        var show = _viewModel?.IsImmersiveAudio == true;

        // 音频或视频沉浸打开时，底部播放条/状态栏都让位；
        // 视频阶段不绘制 Avalonia 内容覆盖原生视频表面。
        BottomHost.IsVisible = _viewModel?.IsImmersiveOpen != true;

        if (!show && !ImmersiveLayer.IsVisible)
        {
            return;
        }

        var token = ++_immersiveLayerToken;

        if (show)
        {
            ImmersiveLayer.IsVisible = true;

            if (immediate)
            {
                ImmersiveLayer.Opacity = 1;
                ImmersiveLayer.RenderTransform = TransformOperations.Parse("scale(1)");
                return;
            }

            ImmersiveLayer.Opacity = 0;
            ImmersiveLayer.RenderTransform = TransformOperations.Parse("scale(0.985)");

            // 等一帧再切到目标值，让淡入/缩放过渡真正可见。
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (token != _immersiveLayerToken)
                    {
                        return;
                    }

                    ImmersiveLayer.Opacity = 1;
                    ImmersiveLayer.RenderTransform = TransformOperations.Parse("scale(1)");
                },
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render);
            return;
        }

        ImmersiveLayer.Opacity = 0;
        ImmersiveLayer.RenderTransform = TransformOperations.Parse("scale(0.985)");

        DispatcherTimer.RunOnce(
            () =>
            {
                if (token == _immersiveLayerToken && _viewModel?.IsImmersiveAudio != true)
                {
                    ImmersiveLayer.IsVisible = false;
                }
            },
            TimeSpan.FromMilliseconds(180),
            DispatcherPriority.Background);
    }

    private void ApplyLayoutMode(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var mode = width >= ExpandedThreshold
            ? "expanded"
            : width >= CompactThreshold ? "compact" : "narrow";

        MainRoot.Classes.Set("tight", width < 620);

        if (string.Equals(mode, _layoutMode, StringComparison.Ordinal))
        {
            return;
        }

        _layoutMode = mode;
        MainRoot.Classes.Set("expanded", mode == "expanded");
        MainRoot.Classes.Set("compact", mode == "compact");
        MainRoot.Classes.Set("narrow", mode == "narrow");

        // 换显示模式时收起浮层和临时折叠状态，回到该模式的默认形态。
        SetNavigationOverlay(false);
        SetPaneCollapsed(false);
        UpdateNavigationToggleLabels();
        UpdateShellClasses();
    }

    /// <summary>
    /// 计算 rail 类：展开模式收起的窄栏，或未开浮层的紧凑模式。
    /// 窄屏只用 280 浮层抽屉，不存在 rail。
    /// </summary>
    private void UpdateShellClasses()
    {
        var isRail = _layoutMode switch
        {
            "expanded" => _isPaneCollapsed,
            "compact" => !_isNavigationOverlayOpen,
            _ => false,
        };

        MainRoot.Classes.Set("rail", isRail);
    }

    private void SetNavigationOverlay(bool isOpen)
    {
        if (_layoutMode == "expanded")
        {
            isOpen = false;
        }

        if (_isNavigationOverlayOpen == isOpen)
        {
            return;
        }

        _isNavigationOverlayOpen = isOpen;
        MainRoot.Classes.Set("nav-open", isOpen);
        UpdateNavigationToggleLabels();
        UpdateShellClasses();

        if (isOpen)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                },
                DispatcherPriority.Input);
        }
    }

    /// <summary>展开模式把面板收成 48 窄栏；其它模式忽略。</summary>
    private void SetPaneCollapsed(bool isCollapsed)
    {
        if (_layoutMode != "expanded")
        {
            isCollapsed = false;
        }

        if (_isPaneCollapsed == isCollapsed)
        {
            return;
        }

        _isPaneCollapsed = isCollapsed;
        MainRoot.Classes.Set("pane-collapsed", isCollapsed);
        UpdateNavigationToggleLabels();
        UpdateShellClasses();
    }

    /// <summary>面板切换按钮和标题栏汉堡共用 Ctrl+E 的行为；沉浸模式下不打开面板。</summary>
    private void TogglePane()
    {
        if (_viewModel?.IsImmersiveOpen == true)
        {
            return;
        }

        if (_layoutMode == "expanded")
        {
            SetPaneCollapsed(!_isPaneCollapsed);
        }
        else
        {
            SetNavigationOverlay(!_isNavigationOverlayOpen);
        }
    }

    private void UpdateNavigationToggleLabels()
    {
        var paneLabel = _layoutMode switch
        {
            "expanded" => _isPaneCollapsed ? "展开导航菜单" : "收起导航菜单",
            _ => _isNavigationOverlayOpen ? "关闭导航菜单" : "打开导航菜单",
        };

        // 面板按钮的读屏名称保持动作中立的固定文案，当前动作用 tooltip 说明。
        AutomationProperties.SetName(PaneToggleButton, "打开或关闭导航菜单");
        ToolTip.SetTip(PaneToggleButton, $"{paneLabel}（Ctrl+E）");

        var titleLabel = _isNavigationOverlayOpen ? "关闭导航菜单" : "打开导航菜单";
        AutomationProperties.SetName(NavToggleButton, titleLabel);
        ToolTip.SetTip(NavToggleButton, $"{titleLabel}（Ctrl+E）");
    }

    private void FocusNavigationToggle()
    {
        if (_layoutMode == "narrow")
        {
            NavToggleButton.Focus();
        }
        else
        {
            PaneToggleButton.Focus();
        }
    }

    private void OnPaneToggleClick(object? sender, RoutedEventArgs e)
    {
        TogglePane();
    }

    private void OnNavigationToggleClick(object? sender, RoutedEventArgs e)
    {
        SetNavigationOverlay(!_isNavigationOverlayOpen);
    }

    private void OnNavigationScrimClick(object? sender, RoutedEventArgs e)
    {
        SetNavigationOverlay(false);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel?.SubmitSearch();
        e.Handled = true;
    }

    private void OnNavigationListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || NavigationList.SelectedItem is not NavigationItem item)
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.SelectedNavigationItem = item;
        }

        SetNavigationOverlay(false);
        e.Handled = true;
    }

    /// <summary>点击任意导航项都关闭浮层抽屉，包括已选中的那一项。</summary>
    private void OnNavigationListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isNavigationOverlayOpen)
        {
            return;
        }

        if (e.Source is Visual source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
        {
            SetNavigationOverlay(false);
        }
    }

    private void OnMainViewKeyDown(object? sender, KeyEventArgs e)
    {
        // Alt+← 与标题栏返回按钮一致；浮层打开时先让 Esc 处理抽屉。
        if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)
        {
            if (!_isNavigationOverlayOpen && _viewModel?.CanGoBack == true)
            {
                _viewModel.GoBackCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.O)
            {
                _viewModel?.OpenFilesCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                FocusSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.E)
            {
                TogglePane();
                e.Handled = true;
            }

            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel?.IsImmersiveOpen == true)
        {
            _viewModel.CloseImmersive();
            e.Handled = true;
        }
        else if (_isNavigationOverlayOpen)
        {
            SetNavigationOverlay(false);
            FocusNavigationToggle();
            e.Handled = true;
        }
        else if (_viewModel?.IsStatusVisible == true)
        {
            _viewModel.DismissStatusCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FocusSearch()
    {
        // 沉浸播放隐藏了导航面板，Ctrl+F 不强行退出沉浸。
        if (_viewModel?.IsImmersiveOpen == true)
        {
            return;
        }

        // 紧凑/窄屏先打开搜索所在的导航抽屉；展开模式折叠时先恢复面板。
        if (_layoutMode == "expanded" && _isPaneCollapsed)
        {
            SetPaneCollapsed(false);
        }
        else if (_layoutMode is "compact" or "narrow")
        {
            SetNavigationOverlay(true);
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragState(e, true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragState(e, true);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void UpdateDragState(DragEventArgs e, bool entering)
    {
        var hasFiles = e.DataTransfer?.Contains(DataFormat.File) == true;
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = hasFiles && entering;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;

        try
        {
            var transfer = e.DataTransfer;
            if (transfer is null)
            {
                return;
            }

            IStorageItem[]? files = transfer is IAsyncDataTransfer asyncTransfer
                ? await asyncTransfer.TryGetFilesAsync()
                : transfer.TryGetFiles();
            if (files is null || files.Length == 0)
            {
                return;
            }

            var paths = new List<string>();
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            if (paths.Count > 0 && _viewModel is not null)
            {
                await _viewModel.AcceptDroppedPathsAsync(paths).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException)
        {
            _viewModel?.ShowStatus("无法读取拖入的文件。");
        }
        catch (Exception exception)
        {
            _viewModel?.ShowStatus($"无法添加拖入的文件：{exception.Message}");
        }
    }
}
