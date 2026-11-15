using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AvaStudio.Views.Pages;

public partial class SearchPageView : UserControl
{
    public SearchPageView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(
            () =>
            {
                SearchInput.Focus();
                SearchInput.SelectAll();
            },
            DispatcherPriority.Input);
    }
}
