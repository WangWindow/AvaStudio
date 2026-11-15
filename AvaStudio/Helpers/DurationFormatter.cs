namespace AvaStudio.Helpers;

/// <summary>时长格式化工具，统一播放器、歌曲行和卡片的显示格式。</summary>
public static class DurationFormatter
{
    /// <summary>把时长格式化为 <c>m:ss</c> 或 <c>h:mm:ss</c>；未知时长显示 <c>--:--</c>。</summary>
    public static string Format(TimeSpan? duration)
    {
        if (duration is not { } value || value <= TimeSpan.Zero)
        {
            return "--:--";
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    /// <summary>把时长格式化为带“小时/分钟”的完整文本，用于统计摘要。</summary>
    public static string FormatLong(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0:00";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes}:{duration.Seconds:00}";
    }
}
