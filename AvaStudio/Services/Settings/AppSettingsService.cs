using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaStudio.Services.Settings;

/// <summary>
/// 应用设置的本地存储。负责资料库位置、音量、静音和界面偏好；
/// 所有写入使用临时文件加原子替换，避免半写文件。
/// 该类只在 UI 线程创建和使用，媒体扫描通过只读属性获取文件夹列表。
/// </summary>
public sealed class AppSettingsService
{
    /// <summary>设置目录名。</summary>
    private const string SettingsFolderName = "AvaStudio";

    /// <summary>设置文件名。</summary>
    private const string SettingsFileName = "settings.json";

    /// <summary>默认音量（0..100）。</summary>
    public const double DefaultVolume = 80;

    /// <summary>默认语言。</summary>
    public const string DefaultLanguage = "zh-CN";

    /// <summary>默认主题：跟随系统。</summary>
    public const string DefaultTheme = "system";

    /// <summary>默认只允许一个应用实例。</summary>
    public const bool DefaultMultiInstance = false;

    /// <summary>默认自动播放下一项。</summary>
    public const bool DefaultAutoAdvance = true;

    /// <summary>默认记录最近播放；关闭后删除历史记录文件。</summary>
    public const bool DefaultRecordRecentPlayback = true;

    /// <summary>默认使用软件视频渲染；GPU 渲染用于 HDR/高画质，内存占用更高。</summary>
    public const bool DefaultUseGpuVideoRendering = false;

    private readonly string _settingsFilePath;

    public AppSettingsService()
    {
        SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsFolderName);
        _settingsFilePath = Path.Combine(SettingsDirectory, SettingsFileName);

        MusicFolders = [];
        VideoFolders = [];

        if (!TryLoad())
        {
            Volume = DefaultVolume;
            IsMuted = false;
            Theme = DefaultTheme;
            Language = DefaultLanguage;
            AutoAdvance = DefaultAutoAdvance;
            MultiInstance = DefaultMultiInstance;
            RecordRecentPlayback = DefaultRecordRecentPlayback;
            UseGpuVideoRendering = DefaultUseGpuVideoRendering;
        }

    }

    /// <summary>应用数据目录。</summary>
    public string SettingsDirectory { get; }

    /// <summary>设置文件路径。</summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>音乐资料库文件夹。</summary>
    public ObservableCollection<string> MusicFolders { get; }

    /// <summary>视频资料库文件夹。</summary>
    public ObservableCollection<string> VideoFolders { get; }

    /// <summary>主题标识：system / light / dark。</summary>
    public string Theme { get; private set; } = DefaultTheme;

    /// <summary>界面语言标识。</summary>
    public string Language { get; private set; } = DefaultLanguage;

    /// <summary>播放音量 0..100。</summary>
    public double Volume { get; private set; } = DefaultVolume;

    /// <summary>是否静音。</summary>
    public bool IsMuted { get; private set; }

    /// <summary>是否允许多个应用实例；默认关闭，修改后下次启动生效。</summary>
    public bool MultiInstance { get; private set; } = DefaultMultiInstance;

    /// <summary>播放结束后是否自动播放下一项。</summary>
    public bool AutoAdvance { get; private set; } = DefaultAutoAdvance;

    /// <summary>是否记录最近播放；关闭后不再写入并删除已有记录文件。</summary>
    public bool RecordRecentPlayback { get; private set; } = DefaultRecordRecentPlayback;

    /// <summary>
    /// 是否使用 ANGLE/OpenGL 视频渲染；开启后支持 HDR 色调映射和 GPU 缩放，
    /// 但 4K 视频的私有内存会明显上升。修改后下次启动生效。
    /// </summary>
    public bool UseGpuVideoRendering { get; private set; } = DefaultUseGpuVideoRendering;

    /// <summary>任何设置发生变化时触发；事件在 UI 线程触发。</summary>
    public event EventHandler? Changed;

    /// <summary>添加音乐文件夹并立即保存。</summary>
    public SettingsAddResult AddMusicFolder(string path) => AddFolder(MusicFolders, path);

    /// <summary>添加视频文件夹并立即保存。</summary>
    public SettingsAddResult AddVideoFolder(string path) => AddFolder(VideoFolders, path);

    /// <summary>移除音乐文件夹并立即保存。</summary>
    public bool RemoveMusicFolder(string path) => RemoveFolder(MusicFolders, path);

    /// <summary>移除视频文件夹并立即保存。</summary>
    public bool RemoveVideoFolder(string path) => RemoveFolder(VideoFolders, path);

    /// <summary>更新音量；只有值变化时才写盘。</summary>
    public void SetVolume(double volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        if (Math.Abs(clamped - Volume) < 0.01)
        {
            return;
        }

        Volume = clamped;
        SaveAndNotify();
    }

    /// <summary>更新静音状态。</summary>
    public void SetMuted(bool isMuted)
    {
        if (IsMuted == isMuted)
        {
            return;
        }

        IsMuted = isMuted;
        SaveAndNotify();
    }

    /// <summary>更新自动播放下一项。</summary>
    public void SetAutoAdvance(bool autoAdvance)
    {
        if (AutoAdvance == autoAdvance)
        {
            return;
        }

        AutoAdvance = autoAdvance;
        SaveAndNotify();
    }

    /// <summary>更新多实例开关；下次启动时由 Program 读取。</summary>
    public void SetMultiInstance(bool multiInstance)
    {
        if (MultiInstance == multiInstance)
        {
            return;
        }

        MultiInstance = multiInstance;
        SaveAndNotify();
    }

    /// <summary>更新视频渲染模式；下次启动时由视频输出工厂读取。</summary>
    public void SetUseGpuVideoRendering(bool useGpuVideoRendering)
    {
        if (UseGpuVideoRendering == useGpuVideoRendering)
        {
            return;
        }

        UseGpuVideoRendering = useGpuVideoRendering;
        SaveAndNotify();
    }

    /// <summary>更新是否记录最近播放。</summary>
    public void SetRecordRecentPlayback(bool recordRecentPlayback)
    {
        if (RecordRecentPlayback == recordRecentPlayback)
        {
            return;
        }

        RecordRecentPlayback = recordRecentPlayback;
        var saved = TrySave();
        // 即使设置写盘失败，也要让资料库立即清理隐私记录；写盘成功时同样只通知一次。
        if (saved || !recordRecentPlayback)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>更新主题标识。</summary>
    public void SetTheme(string theme)
    {
        if (string.Equals(Theme, theme, StringComparison.Ordinal))
        {
            return;
        }

        Theme = string.IsNullOrWhiteSpace(theme) ? DefaultTheme : theme;
        SaveAndNotify();
    }

    /// <summary>重新读取设置文件；失败时保留当前内存状态。</summary>
    public bool Reload()
    {
        if (!TryLoad())
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>添加文件夹并处理重复/无效路径。</summary>
    private SettingsAddResult AddFolder(ObservableCollection<string> target, string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return SettingsAddResult.InvalidPath;
        }

        if (target.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return SettingsAddResult.Duplicate;
        }

        target.Add(normalized);
        if (!TrySave())
        {
            target.Remove(normalized);
            return SettingsAddResult.SaveFailed;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return SettingsAddResult.Added;
    }

    /// <summary>移除文件夹；保存失败时恢复原位置。</summary>
    private bool RemoveFolder(ObservableCollection<string> target, string path)
    {
        var normalized = NormalizePath(path);
        var existing = target.FirstOrDefault(item =>
            string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        var index = target.IndexOf(existing);
        target.RemoveAt(index);
        if (TrySave())
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        target.Insert(index, existing);
        return false;
    }

    /// <summary>保存并通知监听者。</summary>
    private void SaveAndNotify()
    {
        if (TrySave())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>从磁盘读取设置；文件不存在视为成功并使用默认值。</summary>
    private bool TryLoad()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return true;
            }

            var json = File.ReadAllText(_settingsFilePath);
            var document = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettingsDocument);
            if (document is null)
            {
                return false;
            }

            ReplaceFolders(MusicFolders, document.MusicFolders);
            ReplaceFolders(VideoFolders, document.VideoFolders);
            Theme = string.IsNullOrWhiteSpace(document.Theme) ? DefaultTheme : document.Theme;
            Language = string.IsNullOrWhiteSpace(document.Language) ? DefaultLanguage : document.Language;
            Volume = double.IsFinite(document.Volume) ? Math.Clamp(document.Volume, 0, 100) : DefaultVolume;
            IsMuted = document.IsMuted;
            AutoAdvance = document.AutoAdvance;
            MultiInstance = document.MultiInstance;
            RecordRecentPlayback = document.RecordRecentPlayback;
            UseGpuVideoRendering = document.UseGpuVideoRendering;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>以原子方式保存设置。</summary>
    private bool TrySave()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            var document = new AppSettingsDocument
            {
                MusicFolders = [.. MusicFolders],
                VideoFolders = [.. VideoFolders],
                Theme = Theme,
                Language = Language,
                Volume = Volume,
                IsMuted = IsMuted,
                AutoAdvance = AutoAdvance,
                MultiInstance = MultiInstance,
                RecordRecentPlayback = RecordRecentPlayback,
                UseGpuVideoRendering = UseGpuVideoRendering,
            };
            var json = JsonSerializer.Serialize(document, AppSettingsJsonContext.Default.AppSettingsDocument);
            var temporaryPath = _settingsFilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsFilePath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>替换文件夹集合内容并去重。</summary>
    private static void ReplaceFolders(ObservableCollection<string> target, List<string>? values)
    {
        target.Clear();
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            var normalized = NormalizePath(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!target.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(normalized);
            }
        }
    }

    /// <summary>规范化绝对路径；无效路径返回空字符串。</summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

}

/// <summary>添加资料库位置的结果。</summary>
public enum SettingsAddResult
{
    Added,
    Duplicate,
    InvalidPath,
    SaveFailed,
}

/// <summary>设置文件的数据传输对象。</summary>
internal sealed class AppSettingsDocument
{
    public List<string> MusicFolders { get; set; } = [];

    public List<string> VideoFolders { get; set; } = [];

    public string Theme { get; set; } = AppSettingsService.DefaultTheme;

    public string Language { get; set; } = AppSettingsService.DefaultLanguage;

    public double Volume { get; set; } = AppSettingsService.DefaultVolume;

    public bool IsMuted { get; set; }

    public bool AutoAdvance { get; set; } = AppSettingsService.DefaultAutoAdvance;

    public bool MultiInstance { get; set; } = AppSettingsService.DefaultMultiInstance;

    public bool RecordRecentPlayback { get; set; } = AppSettingsService.DefaultRecordRecentPlayback;

    public bool UseGpuVideoRendering { get; set; } = AppSettingsService.DefaultUseGpuVideoRendering;
}

/// <summary>NativeAOT 友好的设置 JSON 序列化上下文。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettingsDocument))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}

