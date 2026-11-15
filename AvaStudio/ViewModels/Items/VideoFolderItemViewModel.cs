using System;
using CommunityToolkit.Mvvm.Input;
using AvaStudio.Models;

namespace AvaStudio.ViewModels.Items;

/// <summary>视频文件夹卡片的界面包装。</summary>
public sealed class VideoFolderItemViewModel : ViewModelBase
{
    public VideoFolderItemViewModel(VideoDirectoryNode node, Action<VideoFolderItemViewModel>? openAction)
    {
        Node = node;
        OpenCommand = new RelayCommand(() => openAction?.Invoke(this), () => openAction is not null);
    }

    /// <summary>目录树节点。</summary>
    public VideoDirectoryNode Node { get; }

    /// <summary>文件夹名。</summary>
    public string Name => Node.Name;

    /// <summary>完整路径。</summary>
    public string Path => Node.Path;

    /// <summary>文件夹内容摘要；空目录显示“空”，与 Screenbox 文件夹卡一致。</summary>
    public string CountText => Node.ItemCount == 0 ? "空" : $"{Node.ItemCount} 项";

    /// <summary>打开文件夹的命令。</summary>
    public IRelayCommand OpenCommand { get; }
}
