using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace NetLedger.Core;

public sealed record ConnectionInfo(int Pid, string Protocol, IPEndPoint Local, IPEndPoint? Remote, string State);

/// <summary>Live TCP/UDP endpoints via GetExtendedTcpTable / GetExtendedUdpTable.</summary>
public static class ConnectionTable
{
    const int AF_INET = 2, AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
    const uint ERROR_INSUFFICIENT_BUFFER = 122;

    static readonly string[] TcpStates =
    {
        "", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1",
        "FIN wait 2", "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB"
    };

    public static List<ConnectionInfo> GetAll()
    {
        var list = new List<ConnectionInfo>();
        ReadTcp4(list); ReadTcp6(list); ReadUdp4(list); ReadUdp6(list);
        return list;
    }

    static int Port(uint p) => (ushort)IPAddress.NetworkToHostOrder((short)(p & 0xFFFF));
    static string State(uint s) => s < TcpStates.Length ? TcpStates[s] : s.ToString();

    static IntPtr Fetch(bool tcp, int af)
    {
        int size = 0;
        IntPtr buf = IntPtr.Zero;
        for (int i = 0; i < 6; i++)
        {
            uint r = tcp ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                         : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
            if (r == 0 && buf != IntPtr.Zero) return buf;
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
            if (r != ERROR_INSUFFICIENT_BUFFER && r != 0) return IntPtr.Zero;
            buf = Marshal.AllocHGlobal(size + 4096);
            size += 4096;
        }
        if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        return IntPtr.Zero;
    }

    static unsafe void ReadTcp4(List<ConnectionInfo> list)
    {
        IntPtr buf = Fetch(true, AF_INET);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Marshal.ReadInt32(buf);
            uint* row = (uint*)(buf + 4);
            for (int i = 0; i < n; i++, row += 6)
            {
                // state, localAddr, localPort, remoteAddr, remotePort, pid
                var local = new IPEndPoint(new IPAddress(row[1]), Port(row[2]));
                IPEndPoint? remote = row[0] == 2 ? null : new IPEndPoint(new IPAddress(row[3]), Port(row[4]));
                list.Add(new ConnectionInfo((int)row[5], "TCP", local, remote, State(row[0])));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static unsafe void ReadTcp6(List<ConnectionInfo> list)
    {
        IntPtr buf = Fetch(true, AF_INET6);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Marshal.ReadInt32(buf);
            byte* row = (byte*)(buf + 4);
            for (int i = 0; i < n; i++, row += 56)
            {
                // localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                var la = new IPAddress(new ReadOnlySpan<byte>(row, 16), *(uint*)(row + 16));
                int lp = Port(*(uint*)(row + 20));
                var ra = new IPAddress(new ReadOnlySpan<byte>(row + 24, 16), *(uint*)(row + 40));
                int rp = Port(*(uint*)(row + 44));
                uint state = *(uint*)(row + 48);
                int pid = *(int*)(row + 52);
                list.Add(new ConnectionInfo(pid, "TCPv6", new IPEndPoint(la, lp),
                    state == 2 ? null : new IPEndPoint(ra, rp), State(state)));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static unsafe void ReadUdp4(List<ConnectionInfo> list)
    {
        IntPtr buf = Fetch(false, AF_INET);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Marshal.ReadInt32(buf);
            uint* row = (uint*)(buf + 4);
            for (int i = 0; i < n; i++, row += 3)
                list.Add(new ConnectionInfo((int)row[2], "UDP", new IPEndPoint(new IPAddress(row[0]), Port(row[1])), null, ""));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static unsafe void ReadUdp6(List<ConnectionInfo> list)
    {
        IntPtr buf = Fetch(false, AF_INET6);
        if (buf == IntPtr.Zero) return;
        try
        {
            int n = Marshal.ReadInt32(buf);
            byte* row = (byte*)(buf + 4);
            for (int i = 0; i < n; i++, row += 28)
            {
                var la = new IPAddress(new ReadOnlySpan<byte>(row, 16), *(uint*)(row + 16));
                list.Add(new ConnectionInfo(*(int*)(row + 24), "UDPv6", new IPEndPoint(la, Port(*(uint*)(row + 20))), null, ""));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Resets established IPv4 TCP connections owned by matching processes, so a new block rule
    /// takes effect immediately instead of only for new connections.
    /// </summary>
    public static unsafe int CloseTcpConnections(Func<int, bool> pidMatches)
    {
        IntPtr buf = Fetch(true, AF_INET);
        if (buf == IntPtr.Zero) return 0;
        int closed = 0;
        try
        {
            int n = Marshal.ReadInt32(buf);
            uint* row = (uint*)(buf + 4);
            for (int i = 0; i < n; i++, row += 6)
            {
                if (row[0] != 5 || !pidMatches((int)row[5])) continue;
                var r = new MIB_TCPROW { State = 12, LocalAddr = row[1], LocalPort = row[2], RemoteAddr = row[3], RemotePort = row[4] };
                if (SetTcpEntry(ref r) == 0) closed++;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return closed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int cls, int reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int cls, int reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint SetTcpEntry(ref MIB_TCPROW row);
}
