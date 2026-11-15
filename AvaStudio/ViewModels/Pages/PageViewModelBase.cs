using FluentIcons.Common;

namespace AvaStudio.ViewModels.Pages;

/// <summary>页面 ViewModel 的公共基类：标题与导航图标。</summary>
public abstract class PageViewModelBase : ViewModelBase
{
    protected PageViewModelBase(string title, Symbol symbol)
    {
        Title = title;
        Symbol = symbol;
    }

    /// <summary>页面标题，用于窗口标题和页面头部。</summary>
    public string Title { get; }

    /// <summary>导航项使用的图标。</summary>
    public Symbol Symbol { get; }
}
