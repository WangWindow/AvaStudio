using System;
using System.Threading.Tasks;
using AvaStudio.Platform;
using AvaStudio.Services.Library;
using AvaStudio.Services.Playback;
using AvaStudio.Services.Playlists;
using AvaStudio.Services.Settings;
using AvaStudio.ViewModels.Services;

namespace AvaStudio.Services;

/// <summary>
/// 应用组合根：集中创建并持有跨页面共享的服务。
/// 页面 ViewModel 只接收自己需要的服务，避免在视图层重新构造基础设施。
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private bool _disposed;

    public AppServices(DesktopService desktop, Func<IVideoOutput?> videoOutputAccessor)
    {
        Desktop = desktop;
        Settings = new AppSettingsService();
        ArtworkStore = new ArtworkStore(Settings.SettingsDirectory);
        ArtworkLoader = new ArtworkLoader();
        Library = new LibraryService(Settings, ArtworkStore, VideoThumbnailProviderFactory.Create());
        Playlists = new PlaylistStore(Settings);
        Playback = new PlaybackCoordinator(Library, Settings, videoOutputAccessor);
    }

    /// <summary>平台文件选择与系统外壳操作。</summary>
    public DesktopService Desktop { get; }

    /// <summary>应用设置。</summary>
    public AppSettingsService Settings { get; }

    /// <summary>封面提取与缓存。</summary>
    public ArtworkStore ArtworkStore { get; }

    /// <summary>界面封面解码器。</summary>
    public ArtworkLoader ArtworkLoader { get; }

    /// <summary>资料库扫描与聚合。</summary>
    public LibraryService Library { get; }

    /// <summary>播放列表持久化。</summary>
    public PlaylistStore Playlists { get; }

    /// <summary>播放队列与媒体会话协调。</summary>
    public PlaybackCoordinator Playback { get; }

    /// <summary>启动时读取缓存；有资料库位置时在后台刷新索引。</summary>
    public async Task InitializeAsync()
    {
        await Library.InitializeAsync().ConfigureAwait(true);
        if (Settings.MusicFolders.Count > 0 || Settings.VideoFolders.Count > 0)
        {
            _ = Library.RefreshAsync();
        }
    }

    /// <summary>释放播放会话与原生上下文。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Playback.DisposeAsync().ConfigureAwait(false);
    }
}
