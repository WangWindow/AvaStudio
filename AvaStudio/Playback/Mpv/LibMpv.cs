using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AvaStudio.Playback.Mpv;

/// <summary>
/// Minimal libmpv C API surface used by this app. All strings are UTF-8 and every entry
/// point is cdecl so the same declarations work on Windows and Linux. Arguments are passed
/// as an explicit NULL-terminated argv array; paths never go through mpv's command parser.
/// </summary>
internal static partial class LibMpv
{
    internal const string LibraryName = "libmpv";

    internal const int FormatNone = 0;
    internal const int FormatString = 1;
    internal const int FormatFlag = 3;
    internal const int FormatInt64 = 4;
    internal const int FormatDouble = 5;

    internal const int ErrorSuccess = 0;
    internal const int ErrorCommand = -12;

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr mpv_create();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_initialize(IntPtr context);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_set_option_string(IntPtr context, string name, string value);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void mpv_terminate_destroy(IntPtr context);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr mpv_error_string(int error);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial ulong mpv_client_api_version();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_command_async(IntPtr context, ulong replyUserdata, IntPtr args);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void mpv_abort_async_command(IntPtr context, ulong replyUserdata);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_observe_property(IntPtr context, ulong replyUserdata, string name, int format);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_unobserve_property(IntPtr context, ulong registeredReplyUserdata);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_set_property_async(IntPtr context, ulong replyUserdata, string name, int format, IntPtr data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_get_property(IntPtr context, string name, int format, IntPtr data);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr mpv_wait_event(IntPtr context, double timeout);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void mpv_wakeup(IntPtr context);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int mpv_request_log_messages(IntPtr context, string minLevel);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial void mpv_free(IntPtr data);

    internal static string ErrorString(int error)
    {
        IntPtr message = mpv_error_string(error);
        if (message == IntPtr.Zero)
        {
            return $"mpv error {error}";
        }

        return Marshal.PtrToStringUTF8(message) ?? $"mpv error {error}";
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public int EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public int Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

/// <summary>
/// Owns a NULL-terminated UTF-8 argv block. libmpv copies command arguments before
/// <c>mpv_command_async</c> returns, so the block can be released immediately.
/// </summary>
internal sealed class MpvArgv : IDisposable
{
    private readonly IntPtr[] _strings;
    private IntPtr _array;

    private MpvArgv(string[] args)
    {
        _strings = new IntPtr[args.Length];
        _array = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
        for (int i = 0; i < args.Length; i++)
        {
            _strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            Marshal.WriteIntPtr(_array, i * IntPtr.Size, _strings[i]);
        }

        Marshal.WriteIntPtr(_array, args.Length * IntPtr.Size, IntPtr.Zero);
    }

    public IntPtr Pointer => _array;

    public static MpvArgv Create(params string[] args) => new(args);

    public void Dispose()
    {
        if (_array != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_array);
            _array = IntPtr.Zero;
        }

        for (int i = 0; i < _strings.Length; i++)
        {
            if (_strings[i] != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_strings[i]);
                _strings[i] = IntPtr.Zero;
            }
        }
    }
}
