using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OctavianRSM.Services;

// Минимальный резолвер DNS SRV-записей через WinAPI dnsapi.dll
// (в .NET нет встроенного SRV-резолва — Dns.GetHostAddresses умеет только A/AAAA).
// Используется для построения steam://connect/IP:port по домену сервера.
public static class DnsSrvResolver
{
    public sealed record SrvResult(string Target, ushort Port);

    // Резолвит Rust-эндпоинт по домену: SRV _rust._udp.<host> → target+port →
    // A-запись target → "ip:port". Если SRV нет — пробует <host>:defaultPort.
    // Возвращает null, если домен вообще не резолвится.
    public static SrvResult? ResolveRust(string host, int defaultPort)
    {
        var srv = QuerySrv($"_rust._udp.{host}");
        var target = srv?.Target ?? host;
        var port = srv?.Port ?? (ushort)defaultPort;
        try
        {
            var ip = Dns.GetHostAddresses(target)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return ip == null ? null : new SrvResult(ip.ToString(), port);
        }
        catch { return null; }
    }

    // Чистый SRV-запрос: возвращает target и порт первой записи или null
    public static SrvResult? QuerySrv(string name)
    {
        var ptr = IntPtr.Zero;
        try
        {
            // DNS_TYPE_SRV = 33; опции 0 — обычный системный резолвер
            var status = DnsQuery(name, 33, 0, IntPtr.Zero, ref ptr, IntPtr.Zero);
            if (status != 0 || ptr == IntPtr.Zero) return null;

            var cur = ptr;
            while (cur != IntPtr.Zero)
            {
                var rec = Marshal.PtrToStructure<DnsSrvRecord>(cur);
                if (rec.Type == 33 && rec.Target != IntPtr.Zero)
                {
                    var target = Marshal.PtrToStringUni(rec.Target);
                    if (!string.IsNullOrEmpty(target))
                        return new SrvResult(target, rec.Port);
                }
                cur = rec.Next;
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (ptr != IntPtr.Zero) DnsRecordListFree(ptr, 1); // DnsFreeRecordList
        }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode)]
    private static extern int DnsQuery(string name, ushort type, uint options,
        IntPtr servers, ref IntPtr result, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr recordList, int freeType);

    // DNS_RECORD с union-данными SRV (DNS_SRV_DATA):
    // pNext, pName, wType, wDataLength, Flags(DWORD), dwTtl, dwReserved,
    // затем pNameTarget, wPriority, wWeight, wPort
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsSrvRecord
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
        public IntPtr Target;
        public ushort Priority;
        public ushort Weight;
        public ushort Port;
    }
}
