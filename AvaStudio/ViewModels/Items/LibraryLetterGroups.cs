using System.Collections.ObjectModel;

namespace AvaStudio.ViewModels.Items;

/// <summary>专辑网格的字母分组。</summary>
public sealed class AlbumLetterGroup
{
    public AlbumLetterGroup(string header)
    {
        Header = header;
    }

    /// <summary>分组标题：&amp;、# 或 A-Z。</summary>
    public string Header { get; }

    /// <summary>组内专辑。</summary>
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = [];
}

/// <summary>艺术家网格的字母分组。</summary>
public sealed class ArtistLetterGroup
{
    public ArtistLetterGroup(string header)
    {
        Header = header;
    }

    /// <summary>分组标题：&amp;、# 或 A-Z。</summary>
    public string Header { get; }

    /// <summary>组内艺术家。</summary>
    public ObservableCollection<ArtistItemViewModel> Artists { get; } = [];
}
