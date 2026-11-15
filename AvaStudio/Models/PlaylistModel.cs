using System.Collections.ObjectModel;

namespace AvaStudio.Models;

/// <summary>
/// 用户创建的持久化播放列表。列表本身可观察，便于界面直接绑定；
/// 持久化由 <c>PlaylistStore</c> 统一负责，ViewModel 不写文件。
/// </summary>
public sealed class PlaylistModel(string id, string name, DateTimeOffset createdUtc, ObservableCollection<MediaItem>? items = null)
{
    /// <summary>稳定标识，用于后续重命名、删除和持久化。</summary>
    public string Id { get; } = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;

    /// <summary>播放列表名称。</summary>
    public string Name { get; set; } = string.IsNullOrWhiteSpace(name) ? "未命名播放列表" : name;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedUtc { get; } = createdUtc;

    /// <summary>列表中的媒体条目。</summary>
    public ObservableCollection<MediaItem> Items { get; } = items ?? [];

    /// <summary>用于卡片左上角的首字母占位。</summary>
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>总时长。</summary>
    public TimeSpan TotalDuration
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var item in Items)
            {
                total += item.Duration;
            }

            return total;
        }
    }
}
