using Avalonia.Controls;
using AvaStudio.Platform;
using AvaStudio.ViewModels;

namespace AvaStudio.Views;

public partial class MainWindow : Window
{
    private bool _disposeStarted;

    public MainWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // 关闭窗口时先等待播放会话释放 GL 渲染上下文和 libmpv 核心；
        // 取消本次关闭，释放完成后再真正关闭，避免渲染线程仍持有已销毁的 mpv 上下文。
        if (!_disposeStarted && DataContext is MainViewModel viewModel)
        {
            e.Cancel = true;
            _disposeStarted = true;
            try
            {
                await viewModel.DisposeAsync();
            }
            finally
            {
                Close();
            }

            return;
        }

        base.OnClosing(e);
    }
}
