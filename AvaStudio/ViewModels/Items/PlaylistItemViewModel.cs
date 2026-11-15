using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaStudio.Helpers;
using AvaStudio.Models;

namespace AvaStudio.ViewModels.Items;

/// <summary>播放列表卡片的界面包装。</summary>
public sealed partial class PlaylistItemViewModel : ViewModelBase
{
    public PlaylistItemViewModel(
        PlaylistModel model,
        Action<PlaylistItemViewModel>? openAction,
        Action<PlaylistItemViewModel>? playAction,
        Action<PlaylistItemViewModel>? deleteAction)
    {
        Model = model;
        OpenCommand = new RelayCommand(() => openAction?.Invoke(this), () => openAction is not null);
        PlayCommand = new RelayCommand(() => playAction?.Invoke(this), () => playAction is not null && model.Items.Count > 0);
        DeleteCommand = new RelayCommand(() => deleteAction?.Invoke(this), () => deleteAction is not null);
    }

    /// <summary>持久化模型。</summary>
    public PlaylistModel Model { get; }

    /// <summary>播放列表名称。</summary>
    public string Name => Model.Name;

    /// <summary>首字母占位。</summary>
    public string Initial => Model.Initial;

    /// <summary>条目数量文本。</summary>
    public string CountText => Model.Items.Count == 0
        ? "空列表"
        : $"{Model.Items.Count} 个项目 · {DurationFormatter.FormatLong(Model.TotalDuration)}";

    /// <summary>是否为空。</summary>
    public bool IsEmpty => Model.Items.Count == 0;

    /// <summary>打开详情。</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>播放整个列表。</summary>
    public IRelayCommand PlayCommand { get; }

    /// <summary>删除列表。</summary>
    public IRelayCommand DeleteCommand { get; }

    /// <summary>列表内容变化后刷新统计文本与命令状态。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        PlayCommand.NotifyCanExecuteChanged();
    }
}
