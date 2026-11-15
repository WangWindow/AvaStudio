using System;

namespace AvaStudio.Services.MediaTransport;

/// <summary>按平台选择系统媒体控制实现。</summary>
public static class MediaTransportServiceFactory
{
    /// <summary>Windows 上使用 SMTC，其余平台降级为空实现。</summary>
    public static IMediaTransportService Create()
    {
#if WINDOWS_SMTC
        if (OperatingSystem.IsWindows())
        {
            return new SmtcMediaTransportService();
        }
#endif
        return new NoopMediaTransportService();
    }
}
