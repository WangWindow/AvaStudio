using System;

namespace AvaStudio.Services.MediaTransport;

/// <summary>按平台选择系统媒体控制实现。</summary>
public static class MediaTransportServiceFactory
{
    /// <summary>Windows 使用 SMTC，Linux 使用 MPRIS，其余平台降级为空实现。</summary>
    public static IMediaTransportService Create()
    {
#if WINDOWS_SMTC
        if (OperatingSystem.IsWindows())
        {
            return new SmtcMediaTransportService();
        }
#elif LINUX
        if (OperatingSystem.IsLinux())
        {
            return new MprisMediaTransportService();
        }
#endif
        return new NoopMediaTransportService();
    }
}
