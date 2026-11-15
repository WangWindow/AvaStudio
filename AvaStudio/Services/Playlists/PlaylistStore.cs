using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvaStudio.Models;
using AvaStudio.Services.Settings;

namespace AvaStudio.Services.Playlists;

/// <summary>
/// 播放列表持久化存储。负责读取/写入 <c>playlists.json</c>，
/// 并监听每个列表内条目的集合变更以自动保存；界面可直接绑定 <see cref="Playlists"/>。
/// </summary>
public sealed class PlaylistStore
{
    /// <summary>播放列表文件名。</summary>
    private const string PlaylistsFileName = "playlists.json";

    /// <summary>播放列表名称最大长度。</summary>
    private const int MaxNameLength = 64;

    private readonly string _filePath;
    private bool _isLoading;
    private bool _suppressSave;

    public PlaylistStore(AppSettingsService settings)
    {
        _filePath = Path.Combine(settings.SettingsDirectory, PlaylistsFileName);
        Playlists = [];
        Playlists.CollectionChanged += OnPlaylistsChanged;
        Load();
    }

    /// <summary>全部播放列表。</summary>
    public ObservableCollection<PlaylistModel> Playlists { get; }

    /// <summary>播放列表集合或内容发生变化时触发。</summary>
    public event EventHandler? Changed;

    /// <summary>创建播放列表；名称重复时自动追加序号。</summary>
    public PlaylistModel Create(string? name)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "新建播放列表";
        }

        var uniqueName = normalized;
        var suffix = 2;
        while (Playlists.Any(existing =>
                   string.Equals(existing.Name, uniqueName, StringComparison.CurrentCultureIgnoreCase)))
        {
            uniqueName = $"{normalized} ({suffix})";
            suffix++;
        }

        var playlist = new PlaylistModel(Guid.NewGuid().ToString("N"), uniqueName, DateTimeOffset.UtcNow);
        Playlists.Add(playlist);
        return playlist;
    }

    /// <summary>删除播放列表。</summary>
    public bool Delete(PlaylistModel playlist)
    {
        return Playlists.Remove(playlist);
    }

    /// <summary>重命名播放列表。</summary>
    public bool Rename(PlaylistModel playlist, string? name)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(playlist.Name, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        playlist.Name = normalized;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>向播放列表添加条目，跳过重复路径，返回实际添加数量。</summary>
    public int AddItems(PlaylistModel playlist, IEnumerable<MediaItem> items)
    {
        var added = 0;
        _suppressSave = true;
        try
        {
            foreach (var item in items)
            {
                if (playlist.Items.Any(existing =>
                        string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                playlist.Items.Add(item);
                added++;
            }
        }
        finally
        {
            _suppressSave = false;
        }

        if (added > 0)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>从播放列表移除条目。</summary>
    public bool RemoveItem(PlaylistModel playlist, MediaItem item)
    {
        return playlist.Items.Remove(item);
    }

    /// <summary>把条目移动到新的索引位置。</summary>
    public bool MoveItem(PlaylistModel playlist, int fromIndex, int toIndex)
    {
        if (fromIndex < 0
            || fromIndex >= playlist.Items.Count
            || toIndex < 0
            || toIndex >= playlist.Items.Count
            || fromIndex == toIndex)
        {
            return false;
        }

        playlist.Items.Move(fromIndex, toIndex);
        return true;
    }

    /// <summary>读取播放列表文件；损坏时回退为空集合并保持可写。</summary>
    private void Load()
    {
        _isLoading = true;
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var document = JsonSerializer.Deserialize(json, PlaylistJsonContext.Default.PlaylistDocument);
            if (document?.Playlists is null)
            {
                return;
            }

            foreach (var dto in document.Playlists)
            {
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    continue;
                }

                var items = new ObservableCollection<MediaItem>(
                    (dto.Items ?? []).Where(static item => !string.IsNullOrWhiteSpace(item.Path)));
                var playlist = new PlaylistModel(dto.Id, dto.Name, dto.CreatedUtc, items);
                Playlists.Add(playlist);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 播放列表文件损坏时保留空集合，用户仍可重新创建。
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>原子保存全部播放列表。</summary>
    private void Save()
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var document = new PlaylistDocument
            {
                Playlists = Playlists
                    .Select(static playlist => new PlaylistDto
                    {
                        Id = playlist.Id,
                        Name = playlist.Name,
                        CreatedUtc = playlist.CreatedUtc,
                        Items = [.. playlist.Items],
                    })
                    .ToList(),
            };
            var json = JsonSerializer.Serialize(document, PlaylistJsonContext.Default.PlaylistDocument);
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 保存失败时本次会话仍可使用，下次写入会重试。
        }
    }

    /// <summary>去除名称首尾空白并限制长度。</summary>
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        return trimmed.Length <= MaxNameLength ? trimmed : trimmed[..MaxNameLength];
    }

    private void Hook(PlaylistModel playlist)
    {
        playlist.Items.CollectionChanged += OnPlaylistItemsChanged;
    }

    private void Unhook(PlaylistModel playlist)
    {
        playlist.Items.CollectionChanged -= OnPlaylistItemsChanged;
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (PlaylistModel playlist in e.NewItems)
            {
                Hook(playlist);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (PlaylistModel playlist in e.OldItems)
            {
                Unhook(playlist);
            }
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaylistItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressSave)
        {
            return;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>播放列表 JSON 文档。</summary>
internal sealed class PlaylistDocument
{
    public List<PlaylistDto> Playlists { get; set; } = [];
}

/// <summary>单个播放列表的传输对象。</summary>
internal sealed class PlaylistDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public List<MediaItem> Items { get; set; } = [];
}

/// <summary>NativeAOT 友好的播放列表 JSON 序列化上下文。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PlaylistDocument))]
internal sealed partial class PlaylistJsonContext : JsonSerializerContext
{
}

