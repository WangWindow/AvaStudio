using System;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using AvaStudio.ViewModels.Services;

namespace AvaStudio.ViewModels.Pages;

public sealed partial class NetworkPageViewModel : PageViewModelBase
{
    private const string ConnectedDevicesUri = "ms-settings:connecteddevices";

    private readonly DesktopService _desktop;
    private readonly Action<string> _reportStatus;

    public NetworkPageViewModel(DesktopService desktop, Action<string> reportStatus)
        : base("媒体服务器", Symbol.Server)
    {
        _desktop = desktop;
        _reportStatus = reportStatus;
    }

    [RelayCommand]
    private void OpenDeviceSettings()
    {
        if (!_desktop.OpenUri(ConnectedDevicesUri))
        {
            _reportStatus("无法打开系统设备设置。");
        }
    }
}
