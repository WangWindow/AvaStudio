using FluentIcons.Common;
using AvaStudio.ViewModels.Pages;

namespace AvaStudio.ViewModels;

/// <summary>主导航项：标签、图标和对应的页面实例。</summary>
public sealed class NavigationItem
{
    public NavigationItem(string label, Symbol icon, PageViewModelBase page, bool showSeparator = false)
    {
        Label = label;
        Icon = icon;
        Page = page;
        ShowSeparator = showSeparator;
    }

    /// <summary>导航显示文本。</summary>
    public string Label { get; }

    /// <summary>导航图标。</summary>
    public Symbol Icon { get; }

    /// <summary>该导航项对应的页面。</summary>
    public PageViewModelBase Page { get; }

    /// <summary>是否在导航项上方显示分组分隔线。</summary>
    public bool ShowSeparator { get; }
}
