using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.Models;
using AvaStudio.Services;

namespace AvaStudio.ViewModels.Pages;

/// <summary>
/// 搜索页：对资料库与播放队列中的标题、艺术家、专辑做不区分大小写的包含匹配。
/// 结果按路径去重，并复用播放器的队列/上下文逻辑。
/// </summary>
public sealed partial class SearchPageViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly Action _openLibrarySettings;

    public SearchPageViewModel(AppServices services, Action openLibrarySettings)
        : base("搜索", Symbol.Search)
    {
        _services = services;
        _openLibrarySettings = openLibrarySettings;
        _services.Library.Changed += OnLibraryChanged;
        _services.Playback.Queue.CollectionChanged += OnQueueChanged;
    }

    /// <summary>搜索结果。</summary>
    public ObservableCollection<MediaItemViewModel> Results { get; } = [];

    /// <summary>查询文本。</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>是否有查询文本。</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>是否有结果。</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>是否显示初始提示。</summary>
    public bool IsPromptVisible => !HasQuery;

    /// <summary>是否显示无结果状态。</summary>
    public bool IsNoResultsVisible => HasQuery && Results.Count == 0;

    /// <summary>页面标题。</summary>
    public string SearchTitle => HasQuery ? $"搜索“{Query.Trim()}”" : "搜索";

    /// <summary>结果摘要。</summary>
    public string ResultSummary => Results.Count == 0
        ? "暂无结果"
        : $"{Results.Count} 个结果";

    /// <summary>无结果标题。</summary>
    public string NoResultsTitle => $"没有找到“{Query.Trim()}”";

    partial void OnQueryChanged(string value)
    {
        RefreshResults();
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(SearchTitle));
        OnPropertyChanged(nameof(IsPromptVisible));
        OnPropertyChanged(nameof(IsNoResultsVisible));
        OnPropertyChanged(nameof(NoResultsTitle));
    }

    /// <summary>清空查询。</summary>
    [RelayCommand]
    private void ClearQuery()
    {
        Query = string.Empty;
    }

    /// <summary>打开资料库设置。</summary>
    [RelayCommand]
    private void OpenLibrarySettings()
    {
        _openLibrarySettings();
    }

    /// <summary>播放搜索结果，以全部结果为上下文。</summary>
    private async Task PlayResultAsync(MediaItemViewModel item)
    {
        var context = Results.Select(static result => result.Item).ToArray();
        await _services.Playback.PlayAsync(item.Item, context).ConfigureAwait(true);
    }

    private void RefreshResults()
    {
        var query = Query.Trim();
        Results.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            RaiseResultProperties();
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = _services.Library.Snapshot.Items
            .Concat(_services.Playback.Queue)
            .Where(static item => item is not null)
            .OrderBy(static item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase);

        foreach (var item in candidates)
        {
            if (!Matches(item, query) || !seen.Add(item.Path))
            {
                continue;
            }

            Results.Add(new MediaItemViewModel(
                item,
                _services.ArtworkLoader,
                result => _ = PlayResultAsync(result)));
        }

        RaiseResultProperties();
    }

    /// <summary>标题、艺术家、专辑任一包含查询即命中。</summary>
    private static bool Matches(MediaItem item, string query) =>
        item.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.DisplayArtist.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.DisplayAlbum.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || item.DisplayGenre.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void RaiseResultProperties()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsNoResultsVisible));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        RefreshResults();
    }

    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshResults();
    }
}
