#if LINUX
using System.IO;
using System.Runtime.InteropServices;

namespace AvaStudio.Platform.Linux;

/// <summary>
/// Linux layout: <c>libmpv/&lt;linux-rid&gt;/libmpv.so.2</c>, falling back to
/// <c>libmpv.so</c>. System library paths are never searched.
/// </summary>
public sealed class LinuxNativeLibraryLocator : INativeLibraryLocator
{
    private static readonly string[] CandidateFileNames = ["libmpv.so.2", "libmpv.so"];

    public string FileName => CandidateFileNames[0];

    public string ResolveLibraryPath()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => throw new PlatformNotSupportedException("libmpv is only supported on Linux x64 and arm64."),
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "libmpv", rid);
        foreach (string fileName in CandidateFileNames)
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string expectedPath = Path.Combine(directory, CandidateFileNames[0]);
        throw new FileNotFoundException(
            $"无法加载 libmpv 播放引擎。请将原生库文件放到 {expectedPath}。",
            expectedPath);
    }
}
#endif
