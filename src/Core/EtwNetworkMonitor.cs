using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NetLedger.Core;

/// <summary>
/// Minimal real-time ETW consumer for the Microsoft-Windows-Kernel-Network provider
/// (per-process bytes sent/received) and Microsoft-Windows-Kernel-Process (process starts,
/// so short-lived processes can still be named after they exit).
/// Written against the raw advapi32 API so it works inside a single-file .exe with no native helpers.
/// </summary>
public sealed unsafe class EtwNetworkMonitor : IDisposable
{
    public const string SessionName = "NetLedger-Network";

    static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    static readonly byte[] DevicePattern = Encoding.Unicode.GetBytes(@"\Device\");

    const ulong InvalidTraceHandle = ulong.MaxValue;
    const int PropsSize = 120;            // sizeof(EVENT_TRACE_PROPERTIES) on x64
    const int PropsTotal = PropsSize + 2048;
    const int LogfileSize = 448;          // sizeof(EVENT_TRACE_LOGFILEW) on x64

    public delegate void TrafficHandler(int pid, int bytes, bool sent);
    public event TrafficHandler? Traffic;
    public event Action<int, string?>? ProcessStarted;

    long _events;
    public long EventsSeen => Interlocked.Read(ref _events);

    ulong _session;
    ulong _trace = InvalidTraceHandle;
    IntPtr _props, _logfile, _namePtr;
    Thread? _thread;
    bool _disposed;
    readonly EventRecordCallback _callback;

    public EtwNetworkMonitor() { _callback = OnEvent; }

    public void Start()
    {
        StopSession(SessionName); // clean up a session left behind by a crash

        _props = AllocProps(SessionName);
        int err = StartTraceW(out _session, SessionName, _props);
        if (err != 0) throw new Win32Exception(err, $"Could not start the network trace session (error {err}).");

        Guid net = KernelNetworkProvider;
        err = EnableTraceEx2(_session, ref net, 1 /*enable*/, 5 /*verbose*/, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (err != 0) throw new Win32Exception(err, $"Could not enable kernel network events (error {err}).");

        Guid proc = KernelProcessProvider;
        EnableTraceEx2(_session, ref proc, 1, 4, 0x10 /*WINEVENT_KEYWORD_PROCESS*/, 0, 0, IntPtr.Zero); // best effort

        _namePtr = Marshal.StringToHGlobalUni(SessionName);
        _logfile = (IntPtr)NativeMemory.AllocZeroed(LogfileSize);
        Marshal.WriteIntPtr(_logfile, 8, _namePtr);                                  // LoggerName
        Marshal.WriteInt32(_logfile, 28, 0x00000100 | 0x10000000);                   // REAL_TIME | EVENT_RECORD
        Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback

        _trace = OpenTraceW(_logfile);
        if (_trace == InvalidTraceHandle)
        {
            int le = Marshal.GetLastWin32Error();
            throw new Win32Exception(le, $"Could not open the trace session (error {le}).");
        }

        _thread = new Thread(() =>
        {
            var handles = new[] { _trace };
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        })
        { IsBackground = true, Name = "ETW consumer" };
        _thread.Start();
    }

    void OnEvent(IntPtr record)
    {
        try
        {
            byte* p = (byte*)record;
            Guid provider = *(Guid*)(p + 24);           // EVENT_HEADER.ProviderId
            ushort id = *(ushort*)(p + 40);             // EVENT_DESCRIPTOR.Id
            int len = *(ushort*)(p + 86);               // UserDataLength
            byte* data = *(byte**)(p + 96);             // UserData
            if (data == null || len < 8) return;

            if (provider == KernelProcessProvider)
            {
                if (id == 1) ProcessStarted?.Invoke(*(int*)data, FindNtPath(data, len));
                return;
            }
            if (provider != KernelNetworkProvider) return;

            bool sent, v6;
            switch (id)
            {
                case 10: case 42: sent = true; v6 = false; break;   // TCP/UDP IPv4 send
                case 11: case 43: sent = false; v6 = false; break;  // TCP/UDP IPv4 receive
                case 26: case 58: sent = true; v6 = true; break;    // TCP/UDP IPv6 send
                case 27: case 59: sent = false; v6 = true; break;   // TCP/UDP IPv6 receive
                default: return;
            }
            if (len < (v6 ? 40 : 16)) return;

            int pid = *(int*)data;
            int size = *(int*)(data + 4);
            // Skip loopback traffic: it never leaves the PC.
            if (v6 ? IsLoopback6(data + 8) || IsLoopback6(data + 24) : data[8] == 127 || data[12] == 127) return;

            Interlocked.Increment(ref _events);
            Traffic?.Invoke(pid, size, sent);
        }
        catch
        {
            // Never let an exception cross back into native code.
        }
    }

    static bool IsLoopback6(byte* a)
    {
        bool zero10 = true;
        for (int i = 0; i < 10; i++) if (a[i] != 0) { zero10 = false; break; }
        if (!zero10) return false;
        if (a[10] == 0xFF && a[11] == 0xFF) return a[12] == 127;             // ::ffff:127.x.x.x
        return a[10] == 0 && a[11] == 0 && a[12] == 0 && a[13] == 0 && a[14] == 0 && a[15] == 1; // ::1
    }

    static string? FindNtPath(byte* data, int len)
    {
        for (int i = 0; i + DevicePattern.Length <= len; i++)
        {
            int j = 0;
            while (j < DevicePattern.Length && data[i + j] == DevicePattern[j]) j++;
            if (j < DevicePattern.Length) continue;
            int end = i;
            while (end + 1 < len && (data[end] != 0 || data[end + 1] != 0)) end += 2;
            return new string((char*)(data + i), 0, (end - i) / 2);
        }
        return null;
    }

    static IntPtr AllocProps(string name)
    {
        IntPtr p = (IntPtr)NativeMemory.AllocZeroed(PropsTotal);
        Marshal.WriteInt32(p, 0, PropsTotal);        // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x00020000);       // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);               // BufferSize (KB)
        Marshal.WriteInt32(p, 64, 0x00000100);       // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);                // FlushTimer (seconds)
        Marshal.WriteInt32(p, 116, PropsSize);       // LoggerNameOffset
        return p;
    }

    /// <summary>Stops a trace session by name (used on start-up and by --uninstall).</summary>
    public static void StopSession(string name)
    {
        IntPtr p = AllocProps(name);
        try { ControlTraceW(0, name, p, 1 /*EVENT_TRACE_CONTROL_STOP*/); }
        finally { NativeMemory.Free((void*)p); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_session != 0 && _props != IntPtr.Zero) ControlTraceW(_session, null, _props, 1);
        if (_trace != InvalidTraceHandle) CloseTrace(_trace);
        _thread?.Join(3000);
        if (_props != IntPtr.Zero) NativeMemory.Free((void*)_props);
        if (_logfile != IntPtr.Zero) NativeMemory.Free((void*)_logfile);
        if (_namePtr != IntPtr.Zero) Marshal.FreeHGlobal(_namePtr);
        _props = _logfile = _namePtr = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int StartTraceW(out ulong handle, string name, IntPtr props);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int ControlTraceW(ulong handle, string? name, IntPtr props, uint code);

    [DllImport("advapi32.dll")]
    static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    static extern int ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    static extern int CloseTrace(ulong handle);
}
