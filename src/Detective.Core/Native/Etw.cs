using System.Runtime.InteropServices;

namespace Detective.Core.Native;

/// <summary>
/// Per-process network bytes from a real-time ETW session on Microsoft-Windows-Kernel-Network. No standard-user
/// API reports network traffic per process, and real-time ETW sessions need administrator rights, so
/// <see cref="TryStart"/> returns null when Detective isn't elevated.
/// Structures are declared as raw x64 offsets: TraceEvent and other managed wrappers aren't Native AOT-safe.
/// </summary>
public sealed unsafe class NetworkTrace : IDisposable
{
    private const string SessionName = "Detective-Network";
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    // Send/receive events for TCP and UDP over IPv4 and IPv6. Every one starts with UInt32 PID, UInt32 size.
    private const ushort TcpSendV4 = 10, TcpRecvV4 = 11, TcpSendV6 = 26, TcpRecvV6 = 27;
    private const ushort UdpSendV4 = 42, UdpRecvV4 = 43, UdpSendV6 = 58, UdpRecvV6 = 59;
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;

    private const int PropertiesSize = 120;
    private const int PropertiesBufferSize = PropertiesSize + 256; // room for the session name
    private const int LogFileSize = 448;

    private static NetworkTrace? _current;

    private readonly Lock _lock = new();
    private Dictionary<int, long> _bytes = new();
    private Dictionary<int, long> _spare = new();
    private readonly ulong _session;
    private ulong _trace;
    private readonly Thread _thread;
    private int _disposed;

    private NetworkTrace(ulong session, ulong trace)
    {
        _session = session;
        _trace = trace;
        _thread = new Thread(() =>
        {
            ulong handle = _trace;
            ProcessTrace(&handle, 1, null, null);
        })
        { IsBackground = true, Name = "ETW network" };
        _thread.Start();
    }

    /// <summary>Starts the session, or returns null (not elevated, or ETW unavailable).</summary>
    public static NetworkTrace? TryStart()
    {
        if (_current is not null) return null;

        // A session left behind by a crashed run would make StartTrace fail with ERROR_ALREADY_EXISTS.
        Stop(0);

        byte* props = stackalloc byte[PropertiesBufferSize];
        InitProperties(props);
        int status = StartTraceW(out ulong session, SessionName, props);
        if (status != 0) return null;

        var guid = KernelNetwork;
        status = EnableTraceEx2(session, &guid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE,
            KeywordIpv4 | KeywordIpv6, 0, 0, null);
        if (status != 0)
        {
            Stop(session);
            return null;
        }

        byte* log = stackalloc byte[LogFileSize];
        new Span<byte>(log, LogFileSize).Clear();
        fixed (char* name = SessionName)
        {
            *(char**)(log + 8) = name;                                      // LoggerName
            *(uint*)(log + 28) = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
            *(delegate* unmanaged<byte*, void>*)(log + 424) = &OnEvent;     // EventRecordCallback
            ulong trace = OpenTraceW(log);
            if (trace == INVALID_PROCESSTRACE_HANDLE)
            {
                Stop(session);
                return null;
            }
            var instance = new NetworkTrace(session, trace);
            _current = instance;
            return instance;
        }
    }

    /// <summary>Bytes sent plus received per PID since the previous call.</summary>
    public Dictionary<int, long> TakeBytes()
    {
        lock (_lock)
        {
            var taken = _bytes;
            _spare.Clear();
            _bytes = _spare;
            _spare = taken;
            return taken;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(byte* record)
    {
        // EVENT_RECORD: EventHeader.EventDescriptor.Id at 40, UserDataLength at 86, UserData at 96.
        ushort id = *(ushort*)(record + 40);
        if (id is not (TcpSendV4 or TcpRecvV4 or TcpSendV6 or TcpRecvV6 or UdpSendV4 or UdpRecvV4 or UdpSendV6 or UdpRecvV6))
            return;
        // Payload: UInt32 PID, UInt32 size, then destination and source addresses (4 bytes each for IPv4,
        // 16 for IPv6).
        bool v6 = id is TcpSendV6 or TcpRecvV6 or UdpSendV6 or UdpRecvV6;
        int addressSize = v6 ? 16 : 4;
        if (*(ushort*)(record + 86) < 8 + 2 * addressSize) return;
        byte* data = *(byte**)(record + 96);
        int pid = (int)*(uint*)data;
        uint size = *(uint*)(data + 4);
        // Loopback traffic between local processes never reaches an adapter; counting it would put the column
        // far above the Networks page.
        if (IsLoopback(data + 8, v6) || IsLoopback(data + 8 + addressSize, v6)) return;

        var trace = _current;
        if (trace is null) return;
        lock (trace._lock)
        {
            ref long total = ref CollectionsMarshal.GetValueRefOrAddDefault(trace._bytes, pid, out _);
            total += size;
        }
    }

    /// <summary>127.0.0.0/8, ::1, or IPv4-mapped 127.x (::ffff:127.x.x.x).</summary>
    internal static bool IsLoopback(byte* address, bool v6)
    {
        if (!v6) return address[0] == 127;
        for (int i = 0; i < 10; i++)
            if (address[i] != 0) return false;
        if (address[10] == 0xFF && address[11] == 0xFF) return address[12] == 127;
        return address[10] == 0 && address[11] == 0 && address[12] == 0 && address[13] == 0 && address[14] == 0 && address[15] == 1;
    }

    private static void InitProperties(byte* props)
    {
        new Span<byte>(props, PropertiesBufferSize).Clear();
        *(uint*)(props + 0) = PropertiesBufferSize;              // Wnode.BufferSize
        *(uint*)(props + 40) = 1;                                // Wnode.ClientContext: QPC timestamps
        *(uint*)(props + 44) = WNODE_FLAG_TRACED_GUID;           // Wnode.Flags
        *(uint*)(props + 64) = EVENT_TRACE_REAL_TIME_MODE;       // LogFileMode
        *(uint*)(props + 68) = 1;                                // FlushTimer: deliver at least every second
        *(uint*)(props + 116) = PropertiesSize;                  // LoggerNameOffset
    }

    private static void Stop(ulong session)
    {
        byte* props = stackalloc byte[PropertiesBufferSize];
        InitProperties(props);
        ControlTraceW(session, session == 0 ? SessionName : null, props, EVENT_TRACE_CONTROL_STOP);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop(_session);
        CloseTrace(_trace);
        _thread.Join(TimeSpan.FromSeconds(2));
        _current = null;
    }

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong session, string name, byte* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong session, string? name, byte* properties, uint control);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong session, Guid* provider, uint control, byte level,
        ulong matchAny, ulong matchAll, uint timeout, void* parameters);

    [DllImport("advapi32.dll")]
    private static extern ulong OpenTraceW(byte* logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong* handles, uint count, void* start, void* end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong trace);
}
