using Avalonia.Controls;

namespace AvaStudio.Views;

/// <summary>
/// 音频独立播放页：大封面 + 标题/副标题。退出、进度和传输控制由共享标题栏
/// 与底部 Popup 提供，因此这里不再持有滑杆或播放命令。
/// </summary>
public partial class ImmersivePlayerView : UserControl
{
    public ImmersivePlayerView()
    {
        InitializeComponent();
    }
}
