# AvaStudio

一个基于 **.NET 11 + Avalonia 12** 的本地桌面媒体播放器：

- 本地优先，音频与视频统一在同一个资料库中管理
- 由 **libmpv** 驱动的播放引擎
- Windows Shell / Linux libmpv 视频缩略图
- Windows 原生视频表面；Linux 使用 OpenGL 渲染，并保留软件渲染回退
- 音频/视频元数据、封面、播放列表、播放队列、搜索与排序
- Windows SMTC / Linux MPRIS 系统媒体控制
- 最近播放记录可关闭；关闭时删除对应记录文件

## 技术栈

| 类别 | 技术 |
| --- | --- |
| UI | Avalonia 12、FluentIcons.Avalonia、CommunityToolkit.Mvvm |
| 播放引擎 | libmpv（项目内自建 P/Invoke 封装） |
| 元数据 | TagLibSharp |
| 搜索/排序 | TinyPinyin |
| Linux 系统媒体控制 | Tmds.DBus.Protocol |
| Windows 打包 | WiX Toolset（MSI） |

## 开发与运行

需要 **.NET 11 SDK**。在仓库根目录运行：

```bash
dotnet restore AvaStudio.slnx
dotnet build AvaStudio.slnx -c Debug --no-restore
dotnet run --project AvaStudio.Desktop
```

### libmpv 原生库

应用不从系统路径加载 libmpv，请将运行库放到仓库根目录的 `runtimes/libmpv/<rid>/`：

```text
runtimes/libmpv/win-x64/libmpv-2.dll
runtimes/libmpv/linux-x64/libmpv.so.2
```

构建时会自动复制到输出目录的 `libmpv/<rid>/`。

Linux 的 libmpv 还需要对应的运行依赖
（如 `libmujs`、`libluajit`、`libopenal`、`libsixel`、`libXpresent`）。
如果目标环境没有可用的 OpenGL 集成，可设置：

```bash
AVASTUDIO_LINUX_VIDEO=software dotnet run --project AvaStudio.Desktop
```

## 许可证

本项目使用 MIT 许可证，详见 [LICENSE](LICENSE)。
