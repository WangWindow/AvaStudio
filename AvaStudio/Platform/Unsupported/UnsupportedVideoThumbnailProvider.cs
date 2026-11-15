using AvaStudio.Services.Library;

namespace AvaStudio.Platform.Unsupported;

/// <summary>
/// 没有系统缩略图能力的平台上的退化实现。
/// 始终返回 null，资料库扫描会把它当作“没有封面”处理，不影响其它元数据。
/// </summary>
public sealed class UnsupportedVideoThumbnailProvider : IVideoThumbnailProvider
{
    /// <inheritdoc />
    public byte[]? GetThumbnail(string videoPath, int width, int height) => null;
}
