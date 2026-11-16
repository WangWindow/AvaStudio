using System;
using CommunityToolkit.Mvvm.Input;
using AvaStudio.Models;

namespace AvaStudio.ViewModels.Items;

/// <summary>视频页路径导航中的一节；当前节不可点击，其余节跳转到对应目录。</summary>
public sealed class VideoBreadcrumbItem
{
    public VideoBreadcrumbItem(
        string name,
        VideoDirectoryNode node,
        bool isFirst,
        bool isCurrent,
        Action<VideoDirectoryNode> navigate)
    {
        Name = name;
        IsFirst = isFirst;
        IsCurrent = isCurrent;
        NavigateCommand = new RelayCommand(() => navigate(node), () => !isCurrent);
    }

    /// <summary>显示名称。</summary>
    public string Name { get; }

    /// <summary>是否为路径第一节；用于隐藏前面的分隔箭头。</summary>
    public bool IsFirst { get; }

    /// <summary>是否为当前目录；当前节显示为普通文本。</summary>
    public bool IsCurrent { get; }

    /// <summary>跳转到该节对应的目录。</summary>
    public IRelayCommand NavigateCommand { get; }
}
