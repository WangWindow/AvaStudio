using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaStudio.ViewModels.Pages;

namespace AvaStudio.Views.Pages;

public partial class PlaylistsPageView : UserControl
{
    private PlaylistsPageViewModel? _viewModel;

    public PlaylistsPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>创建表单打开时自动聚焦名称输入框；表单本身始终在可视树中，只是隐藏。</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as PlaylistsPageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistsPageViewModel.IsCreateFormOpen)
            && _viewModel?.IsCreateFormOpen == true)
        {
            FocusNameBox();
        }
    }

    private void FocusNameBox()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                PlaylistNameBox.Focus();
                PlaylistNameBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnPlaylistNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PlaylistsPageViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (viewModel.CreatePlaylistCommand.CanExecute(null))
            {
                viewModel.CreatePlaylistCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.CancelCreatePlaylistCommand.Execute(null);
            e.Handled = true;
        }
    }
}
