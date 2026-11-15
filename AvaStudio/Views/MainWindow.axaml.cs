using Avalonia.Controls;
using AvaStudio.Platform;
using AvaStudio.ViewModels;

namespace AvaStudio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowAppearance.Apply(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 关闭窗口时释放 libmpv 事件循环与原生上下文；进程退出不等待该任务。
        if (DataContext is MainViewModel viewModel)
        {
            _ = viewModel.DisposeAsync();
        }

        base.OnClosing(e);
    }
}
