using System.Text.Json;
using System.Text.Json.Serialization;
using AvaStudio.Models;
using AvaStudio.Services.Settings;

namespace AvaStudio.Services.Library;

/// <summary>
/// 本地资料库：枚举配置目录、读取标签、缓存元数据并按歌曲/专辑/艺术家/视频文件夹聚合。
/// 扫描在后台线程完成；完成后的不可变 <see cref="Snapshot"/> 在 UI 线程替换，
/// 页面 ViewModel 只消费快照，不直接访问文件系统。
/// </summary>
public sealed class LibraryService
{
    /// <summary>资料库缓存文件名。</summary>
    private const string CacheFileName = "library-cache.json";

    /// <summary>最近播放记录文件名。</summary>
    private const string RecentFileName = "recent.json";

    /// <summary>最近播放最多保留的条目数。</summary>
    private const int MaxRecentItems = 30;

    /// <summary>并发读取标签的最大文件数。</summary>
    private const int MetadataParallelism = 4;

    /// <summary>扫描时跳过的系统/隐藏文件属性。</summary>
    private const FileAttributes SkippedAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

    private readonly AppSettingsService _settings;
    private readonly MediaMetadataReader _reader;
    private readonly string _cacheFilePath;
    private readonly string _recentFilePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private List<MediaItem> _items = [];
    private List<MediaItem> _recentItems = [];
    private HashSet<string> _videoDirectories = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCancellation;
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;

    public LibraryService(
        AppSettingsService settings,
        ArtworkStore artworkStore,
        IVideoThumbnailProvider? videoThumbnailProvider = null)
    {
        _settings = settings;
        _reader = new MediaMetadataReader(artworkStore, videoThumbnailProvider);
        _cacheFilePath = Path.Combine(settings.SettingsDirectory, CacheFileName);
        _recentFilePath = Path.Combine(settings.SettingsDirectory, RecentFileName);
    }

    /// <summary>当前资料库快照；始终非 null。</summary>
    public LibrarySnapshot Snapshot => _snapshot;

    /// <summary>最近播放条目，最新的在前。</summary>
    public IReadOnlyList<MediaItem> RecentItems => _recentItems;

    /// <summary>是否正在扫描。</summary>
    public bool IsScanning { get; private set; }

    /// <summary>当前扫描状态文本，供状态栏/设置页显示。</summary>
    public string ScanStatus { get; private set; } = "尚未扫描资料库";

    /// <summary>资料库内容发生变化时触发。</summary>
    public event EventHandler? Changed;

    /// <summary>最近播放列表发生变化时触发。</summary>
    public event EventHandler? RecentChanged;

    /// <summary>扫描进度；事件在报告线程触发，订阅者应自行调度回 UI。</summary>
    public event EventHandler<LibraryScanProgress>? ScanProgressChanged;

    /// <summary>扫描开始/结束或状态文本变化时触发。</summary>
    public event EventHandler? ScanStateChanged;

    /// <summary>
    /// 读取本机缓存与最近播放记录。不会枚举目录，启动时可以先显示上次结果。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        List<MediaItem>? cachedItems = null;
        List<MediaItem>? recentItems = null;

        await Task.Run(
            () =>
            {
                cachedItems = LoadCache();
                recentItems = LoadRecent();
            },
            cancellationToken).ConfigureAwait(true);

        _items = _settings.MusicFolders.Count + _settings.VideoFolders.Count == 0
            ? []
            : cachedItems ?? [];
        _recentItems = (recentItems ?? [])
            .Where(static item => File.Exists(item.Path))
            .ToList();
        RebuildSnapshot();
        Changed?.Invoke(this, EventArgs.Empty);
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 扫描全部已配置文件夹。取消时保留上一次成功的快照；单文件失败不会中断扫描。
    /// </summary>
    public async Task<LibraryScanResult> RefreshAsync(
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _scanCancellation.Token;

            SetScanState(true, "正在枚举媒体文件…");
            ScanProgressChanged?.Invoke(this, new LibraryScanProgress(0, 0, string.Empty));

            var scannedTree = await Task.Run(() => EnumerateMediaTree(token), token).ConfigureAwait(true);
            _videoDirectories = scannedTree.VideoDirectories;
            var scannedFiles = scannedTree.Files;
            if (scannedFiles.Count == 0)
            {
                _items = [];
                RebuildSnapshot();
                SaveCache();
                SetScanState(
                    false,
                    _settings.MusicFolders.Count + _settings.VideoFolders.Count == 0
                        ? "尚未添加资料库文件夹"
                        : "资料库中没有可用的媒体文件");
                Changed?.Invoke(this, EventArgs.Empty);
                return new LibraryScanResult(0, 0, 0, false, null);
            }

            var cachedByPath = (LoadCache() ?? [])
                .ToDictionary(static item => item.Path, StringComparer.OrdinalIgnoreCase);
            var results = new MediaItem[scannedFiles.Count];
            var updatedCount = 0;
            var reusedCount = 0;
            var completed = 0;

            await Task.Run(
                () =>
                {
                    Parallel.For(
                        0,
                        scannedFiles.Count,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = MetadataParallelism,
                            CancellationToken = token,
                        },
                        index =>
                        {
                            token.ThrowIfCancellationRequested();
                            var scanned = scannedFiles[index];
                            var previous = cachedByPath.GetValueOrDefault(scanned.Path);
                            if (previous is not null && IsUnchanged(previous, scanned))
                            {
                                results[index] = previous with { Kind = scanned.Kind };
                                Interlocked.Increment(ref reusedCount);
                            }
                            else
                            {
                                results[index] = _reader.Read(scanned.Path, previous) with { Kind = scanned.Kind };
                                Interlocked.Increment(ref updatedCount);
                            }

                            var done = Interlocked.Increment(ref completed);
                            if (done == scannedFiles.Count || done % 8 == 0)
                            {
                                var progressValue = new LibraryScanProgress(done, scannedFiles.Count, scanned.Path);
                                ScanProgressChanged?.Invoke(this, progressValue);
                                progress?.Report(progressValue);
                            }
                        });
                },
                token).ConfigureAwait(true);

            token.ThrowIfCancellationRequested();

            _items = results
                .Where(static item => item is not null)
                .OrderBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            RebuildSnapshot();
            SaveCache();

            SetScanState(
                false,
                _items.Count == 0
                    ? "资料库中没有可用的媒体文件"
                    : $"资料库已更新：{_snapshot.MusicItems.Count} 首歌曲，{_snapshot.VideoItems.Count} 个视频");
            Changed?.Invoke(this, EventArgs.Empty);
            return new LibraryScanResult(_items.Count, updatedCount, reusedCount, false, null);
        }
        catch (OperationCanceledException)
        {
            SetScanState(false, "已取消资料库扫描");
            return new LibraryScanResult(_items.Count, 0, 0, true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetScanState(false, $"资料库扫描失败：{exception.Message}");
            return new LibraryScanResult(_items.Count, 0, 0, false, exception);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>更新扫描状态并通知订阅者。</summary>
    private void SetScanState(bool isScanning, string status)
    {
        IsScanning = isScanning;
        ScanStatus = status;
        ScanStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>根据路径查找资料库中的条目。</summary>
    public MediaItem? Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return _items.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 为队列中临时加入的文件读取元数据；已缓存或已在资料库中的路径直接返回快照。
    /// 文件不存在时返回仅含路径的回退条目，由播放层报告真实错误。
    /// </summary>
    public async Task<MediaItem> LoadItemAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var cached = Find(path) ?? _recentItems.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (cached is not null)
        {
            return cached;
        }

        return await Task.Run(() => _reader.Read(path), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>记录一次播放，并更新最近播放列表。</summary>
    public void MarkPlayed(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var played = item.WithLastPlayed(DateTimeOffset.UtcNow);
        _recentItems.RemoveAll(existing =>
            string.Equals(existing.Path, played.Path, StringComparison.OrdinalIgnoreCase));
        _recentItems.Insert(0, played);
        if (_recentItems.Count > MaxRecentItems)
        {
            _recentItems.RemoveRange(MaxRecentItems, _recentItems.Count - MaxRecentItems);
        }

        // 最近播放是轻量数据，直接写盘可以保证异常退出时仍可恢复。
        SaveRecent();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空最近播放记录。</summary>
    public void ClearRecent()
    {
        _recentItems.Clear();
        SaveRecent();
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>重新生成歌曲/专辑/艺术家/视频文件夹聚合快照。</summary>
    private void RebuildSnapshot()
    {
        var musicItems = _items
            .Where(static item => item.Kind == MediaItemKind.Audio)
            .OrderBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var videoItems = _items
            .Where(static item => item.Kind == MediaItemKind.Video)
            .OrderBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var albums = BuildAlbums(musicItems);
        var artists = BuildArtists(musicItems);
        var videoFolders = videoItems
            .GroupBy(static item => Path.GetDirectoryName(item.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(static group => new VideoFolderGroup(
                group.Key,
                group.OrderBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToArray()))
            .OrderBy(static group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var videoRoots = BuildVideoRoots(videoItems);
        _snapshot = new LibrarySnapshot(_items, musicItems, videoItems, albums, artists, videoFolders, videoRoots);
    }

    /// <summary>
    /// 从配置的视频资料库根目录构建目录树。目录集合来自最近一次扫描，
    /// 因此空文件夹也会保留；启动只读缓存时则回退到含视频的目录。
    /// </summary>
    private VideoDirectoryNode[] BuildVideoRoots(IReadOnlyList<MediaItem> videoItems)
    {
        var roots = _settings.VideoFolders
            .Where(Directory.Exists)
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            return [];
        }

        var directories = new HashSet<string>(_videoDirectories, StringComparer.OrdinalIgnoreCase);
        foreach (var item in videoItems)
        {
            var directory = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(directory);
            }
        }

        foreach (var root in roots)
        {
            directories.Add(root);
        }

        var videosByDirectory = videoItems.ToLookup(
            static item => Path.GetDirectoryName(item.Path) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

        return roots
            .Select(root => BuildVideoDirectory(root, directories, videosByDirectory))
            .OrderBy(static node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>递归构建单个视频目录节点；只遍历资料库根目录下的目录。</summary>
    private static VideoDirectoryNode BuildVideoDirectory(
        string path,
        HashSet<string> directories,
        ILookup<string, MediaItem> videosByDirectory)
    {
        var children = directories
            .Where(directory => string.Equals(
                Path.GetDirectoryName(directory),
                path,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static directory => Path.GetFileName(directory), StringComparer.CurrentCultureIgnoreCase)
            .Select(child => BuildVideoDirectory(child, directories, videosByDirectory))
            .ToArray();
        var videos = videosByDirectory[path]
            .OrderBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new VideoDirectoryNode(path, children, videos);
    }

    /// <summary>按专辑名与专辑艺术家聚合音频。</summary>
    private static AlbumGroup[] BuildAlbums(IReadOnlyList<MediaItem> musicItems)
    {
        return musicItems
            .GroupBy(
                static item => $"{item.DisplayAlbum}\u001F{item.DisplayAlbumArtist}",
                StringComparer.CurrentCultureIgnoreCase)
            .Select(
                static group =>
                {
                    var items = group
                        .OrderBy(static item => item.TrackNumber ?? uint.MaxValue)
                        .ThenBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    var first = items[0];
                    var artwork = items.Select(static item => item.ArtworkPath)
                        .FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
                    return new AlbumGroup(
                        group.Key,
                        first.DisplayAlbum,
                        first.DisplayAlbumArtist,
                        artwork,
                        items);
                })
            .OrderBy(static album => album.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static album => album.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>按曲目艺术家聚合音频，并附带其专辑。</summary>
    private static ArtistGroup[] BuildArtists(IReadOnlyList<MediaItem> musicItems)
    {
        return musicItems
            .GroupBy(static item => item.DisplayArtist, StringComparer.CurrentCultureIgnoreCase)
            .Select(
                static group =>
                {
                    var items = group
                        .OrderBy(static item => item.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(static item => item.TrackNumber ?? uint.MaxValue)
                        .ThenBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    var albums = BuildAlbums(items);
                    return new ArtistGroup(group.Key, albums, items);
                })
            .OrderBy(static artist => artist.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 递归枚举所有资料库文件夹中的媒体文件。隐藏、系统与重解析点会被跳过，
    /// 单个目录不可访问不会影响其它目录。
    /// </summary>
    private ScannedTree EnumerateMediaTree(CancellationToken cancellationToken)
    {
        var results = new List<ScannedFile>();
        var videoDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, MediaItemKind Kind)>();
        var configured = _settings.MusicFolders
            .Select(static path => (Path: path, Kind: MediaItemKind.Audio))
            .Concat(_settings.VideoFolders.Select(static path => (Path: path, Kind: MediaItemKind.Video)));

        foreach (var folder in configured)
        {
            if (Directory.Exists(folder.Path))
            {
                pending.Push(folder);
            }
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, configuredKind) = pending.Pop();
            if (configuredKind == MediaItemKind.Video)
            {
                videoDirectories.Add(Path.GetFullPath(directory));
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MediaFileTypes.TryClassify(file, out var kind))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & SkippedAttributes) != 0)
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(file);
                    if (seen.Add(fullPath))
                    {
                        results.Add(new ScannedFile(fullPath, kind == MediaItemKind.Audio ? kind : configuredKind));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 单个文件不可访问时跳过。
                }
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                try
                {
                    var attributes = File.GetAttributes(subdirectory);
                    if ((attributes & SkippedAttributes) == 0)
                    {
                        pending.Push((subdirectory, configuredKind));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 无法读取属性的目录直接跳过。
                }
            }
        }

        return new ScannedTree(results, videoDirectories);
    }

    /// <summary>判断缓存条目是否仍与磁盘文件一致。</summary>
    private static bool IsUnchanged(MediaItem cached, ScannedFile scanned)
    {
        try
        {
            var info = new FileInfo(scanned.Path);
            return info.Exists
                && cached.FileSize == info.Length
                && cached.FileModifiedTicks == info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>读取资料库缓存；失败时返回空列表。</summary>
    private List<MediaItem>? LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var document = JsonSerializer.Deserialize(json, LibraryJsonContext.Default.LibraryCacheDocument);
            // v2 增加了流派字段；v3 增加了视频缩略图（旧缓存中的视频需要重新提取画面帧）。
            return document is { Version: >= 3 } ? document.Items : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>原子写入资料库缓存。</summary>
    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(_settings.SettingsDirectory);
            var document = new LibraryCacheDocument
            {
                // v3：视频条目在缺少内嵌封面时会生成画面缩略图。
                Version = 3,
                SavedUtc = DateTimeOffset.UtcNow,
                Items = [.. _items],
            };
            WriteJsonAtomically(_cacheFilePath, JsonSerializer.Serialize(document, LibraryJsonContext.Default.LibraryCacheDocument));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 缓存写入失败只影响下次启动速度，不影响当前会话。
        }
    }

    /// <summary>读取最近播放记录；失败时返回空列表。</summary>
    private List<MediaItem>? LoadRecent()
    {
        try
        {
            if (!File.Exists(_recentFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_recentFilePath);
            var document = JsonSerializer.Deserialize(json, LibraryJsonContext.Default.RecentDocument);
            return document?.Items?.Take(MaxRecentItems).ToList();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>原子写入最近播放记录。</summary>
    private void SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(_settings.SettingsDirectory);
            var document = new RecentDocument { Items = [.. _recentItems] };
            WriteJsonAtomically(_recentFilePath, JsonSerializer.Serialize(document, LibraryJsonContext.Default.RecentDocument));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 最近播放记录不是关键数据，写入失败时保持内存状态。
        }
    }

    /// <summary>临时文件 + 原子替换，避免进程中断留下半写 JSON。</summary>
    private static void WriteJsonAtomically(string path, string json)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>扫描到的文件及其推断类型。</summary>
    private sealed record ScannedFile(string Path, MediaItemKind Kind);

    /// <summary>一次目录枚举的结果：媒体文件与视频目录集合。</summary>
    private sealed record ScannedTree(List<ScannedFile> Files, HashSet<string> VideoDirectories);
}

/// <summary>一次扫描的结果摘要。</summary>
public sealed record LibraryScanResult(
    int TotalItems,
    int UpdatedItems,
    int ReusedItems,
    bool WasCancelled,
    Exception? Error);

/// <summary>资料库缓存 JSON 文档。</summary>
internal sealed class LibraryCacheDocument
{
    public int Version { get; set; }

    public DateTimeOffset SavedUtc { get; set; }

    public List<MediaItem> Items { get; set; } = [];
}

/// <summary>最近播放 JSON 文档。</summary>
internal sealed class RecentDocument
{
    public List<MediaItem> Items { get; set; } = [];
}

/// <summary>NativeAOT 友好的资料库 JSON 序列化上下文。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LibraryCacheDocument))]
[JsonSerializable(typeof(RecentDocument))]
[JsonSerializable(typeof(List<MediaItem>))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext
{
}
