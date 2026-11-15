namespace AvaStudio.Services.Library;

/// <summary>
/// 视频缩略图提取器：从视频文件中取一帧并编码为图片。
/// 实现必须同步执行、可安全地在资料库扫描的后台线程并发调用，
/// 并且在系统不支持或提取失败时返回 null，而不是抛出异常。
/// </summary>
public interface IVideoThumbnailProvider
{
    /// <summary>
    /// 尝试为指定视频生成缩略图。
    /// </summary>
    /// <param name="videoPath">视频文件的绝对路径。</param>
    /// <param name="width">期望宽度（像素），实现可保持原始宽高比。</param>
    /// <param name="height">期望高度（像素），实现可保持原始宽高比。</param>
    /// <returns>PNG 编码的图片字节；无法提取时返回 null。</returns>
    byte[]? GetThumbnail(string videoPath, int width, int height);
}
