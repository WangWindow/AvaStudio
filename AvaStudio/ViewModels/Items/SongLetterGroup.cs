using System.Collections.ObjectModel;

namespace AvaStudio.ViewModels.Items;

/// <summary>歌曲列表的字母分组，用于复刻 Screenbox 的字母分段表头。</summary>
public sealed class SongLetterGroup
{
    public SongLetterGroup(string header)
    {
        Header = header;
    }

    /// <summary>分组标题：&amp;、# 或 A-Z。</summary>
    public string Header { get; }

    /// <summary>组内歌曲。</summary>
    public ObservableCollection<MediaItemViewModel> Songs { get; } = [];
}
