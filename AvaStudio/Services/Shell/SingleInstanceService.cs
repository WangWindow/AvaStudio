using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AvaStudio.Services.Shell;

/// <summary>
/// Windows 单实例转发：首个进程持有命名互斥量并监听命名管道，
/// 后续进程把命令行参数发给首个进程后退出，避免双击媒体文件时打开多个窗口。
/// </summary>
public static class SingleInstanceService
{
    /// <summary>单实例互斥量；Local 只限制当前登录会话。</summary>
    private const string MutexName = @"Local\AvaStudio.SingleInstance";

    /// <summary>进程间转发通道。</summary>
    private const string PipeName = "AvaStudio.SingleInstance";

    private static readonly object Gate = new();
    private static readonly List<string[]> PendingArguments = [];
    private static Mutex? _mutex;
    private static bool _isPrimary;
    private static bool _initialized;

    /// <summary>收到后续实例参数；可能在后台线程触发，订阅方需切回 UI 线程。</summary>
    public static event Action<string[]>? ArgumentsReceived;

    /// <summary>初始化单实例；返回 false 表示已有实例在运行。</summary>
    public static bool Initialize()
    {
        if (_initialized)
        {
            return _isPrimary;
        }

        _initialized = true;
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 平台暂不引入额外 IPC，保留原有多实例行为。
            _isPrimary = true;
            return true;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _isPrimary = createdNew;
            if (createdNew)
            {
                StartServer();
            }

            return createdNew;
        }
        catch
        {
            // 互斥量不可用时宁可允许多开，也不要阻断正常启动。
            _isPrimary = true;
            return true;
        }
    }

    /// <summary>把参数转发给已存在的首个实例；失败时返回 false。</summary>
    public static bool SendToPrimary(IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(3000);

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', arguments));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>取走订阅前已到达的参数，防止启动阶段的转发丢失。</summary>
    public static IReadOnlyList<string[]> DrainPending()
    {
        lock (Gate)
        {
            var copy = PendingArguments.ToArray();
            PendingArguments.Clear();
            return copy;
        }
    }

    private static void Publish(string[] arguments)
    {
        Action<string[]>? handler;
        lock (Gate)
        {
            handler = ArgumentsReceived;
            if (handler is null)
            {
                PendingArguments.Add(arguments);
                return;
            }
        }

        handler(arguments);
    }

    private static void StartServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                    var arguments = payload.Length == 0
                        ? Array.Empty<string>()
                        : payload.Split('\n');
                    Publish(arguments);
                }
                catch
                {
                    // 管道创建失败时短暂退避，避免空转占满 CPU。
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        });
    }
}
