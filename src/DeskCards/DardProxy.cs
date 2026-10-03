using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCards;

/// <summary>
/// internet 권한 있는 카드의 연결이 지나가는 프록시. 인터넷(공인 주소)에만 이어 주고 내부망은 막는다.
/// 브라우저는 이름 풀이를 하지 않고(규칙으로 막음) 모든 연결을 CONNECT 호스트:포트로 여기에 맡긴다.
/// 여기서 이름을 직접 풀어 공인 주소만 골라 그 IP로 연결하므로, 이름이 내부 주소로 풀리게 바꿔치기(DNS rebinding)해도 못 들어간다.
/// https 페이지에서 http·ws 요청은 브라우저가 이미 막으므로(혼합 콘텐츠) 평문 프록시 요청은 받지 않는다.
/// 127.255.255.1~32 중 하나에 빈 포트를 독점으로 연다.
/// </summary>
internal sealed class DardProxy
{
    private static DardProxy? _instance;
    private readonly Socket _socket;
    private int _blocked;

    private DardProxy(Socket socket)
    {
        _socket = socket;
        _ = Task.Run(AcceptLoop);
    }

    public static DardProxy Instance => _instance ??= new DardProxy(Loopback.Listen());

    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>내부망·잘못된 요청이라 막은 수.</summary>
    public int Blocked => Volatile.Read(ref _blocked);

    private async Task AcceptLoop()
    {
        while (true)
        {
            Socket client;
            try { client = await _socket.AcceptAsync(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(Socket client)
    {
        using var clientStream = new NetworkStream(client, ownsSocket: true);
        Socket? upstream = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string? target = await ReadConnect(clientStream, timeout.Token);
            if (target == null || !TrySplit(target, out string host, out int port))
            {
                Interlocked.Increment(ref _blocked);
                await Reply(clientStream, "405 Method Not Allowed");
                return;
            }
            IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, timeout.Token);
            var allowed = addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0)
            {
                Interlocked.Increment(ref _blocked);
                await Reply(clientStream, "403 Forbidden");
                return;
            }
            upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await upstream.ConnectAsync(allowed, port, timeout.Token); // 검사한 IP로만 잇는다
            await Reply(clientStream, "200 Connection Established");
            using var upstreamStream = new NetworkStream(upstream, ownsSocket: true);
            upstream = null;
            await RelayAsync(clientStream, upstreamStream, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            try { await Reply(clientStream, "502 Bad Gateway"); } catch { }
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    internal static async Task RelayAsync(Stream client, Stream upstream, TimeSpan idle)
    {
        using var lifetime = new CancellationTokenSource(idle);
        async Task Copy(Stream from, Stream to)
        {
            var buffer = new byte[16384];
            int count;
            while ((count = await from.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) != 0)
            {
                lifetime.CancelAfter(idle);
                await to.WriteAsync(buffer.AsMemory(0, count), lifetime.Token).ConfigureAwait(false);
                lifetime.CancelAfter(idle);
            }
        }
        var a = Copy(client, upstream);
        var b = Copy(upstream, client);
        await Task.WhenAny(a, b).ConfigureAwait(false);
        // EOF/오류/유휴 만료 중 하나면 반대 방향도 끝내고, 두 작업의 예외를 모두 관찰한다.
        lifetime.Cancel();
        try { await Task.WhenAll(a, b).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    /// <summary>요청 머리를 읽어 CONNECT 대상(호스트:포트)을 돌려준다. CONNECT가 아니면 null.</summary>
    private static async Task<string?> ReadConnect(Stream s, CancellationToken token)
    {
        var buf = new byte[8192];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len), token);
            if (n == 0) return null;
            len += n;
            string head = Encoding.ASCII.GetString(buf, 0, len);
            int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            string[] first = head[..head.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
            return first.Length == 3 && first[0] == "CONNECT" && end + 4 == len ? first[1] : null;
        }
        return null;
    }

    private static bool TrySplit(string target, out string host, out int port)
    {
        host = "";
        port = 0;
        int colon = target.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
        host = target[..colon].Trim('[', ']');
        return host.Length > 0;
    }

    private static Task Reply(Stream s, string status) =>
        s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();

    /// <summary>
    /// 이어 줘도 되는 인터넷 주소인지. 두 가지를 다 본다(Chrome·OS들이 쓰는 기준과 같은 수준):
    /// 주소 목록(Chrome의 로컬 네트워크 구분)과, 공유기를 거치지 않고 바로 닿는 같은 네트워크인지(iOS·Windows의 내부망 구분).
    /// 공인 IPv6를 받은 집 안 기기는 주소만으로는 모르므로 두 번째로 잡는다.
    /// 공유기 바깥 주소로 되돌아 들어오는 경우(NAT 루프백)는 OS들처럼 막지 않는다.
    /// </summary>
    public static bool IsPublic(IPAddress ip) => IsPublicAddress(ip) && !IsOnLink(ip);

    /// <summary>같은 네트워크(공유기를 거치지 않고 바로 닿는 곳)인지 Windows 경로표에 묻는다. 물을 수 없으면 같은 네트워크로 본다.</summary>
    public static bool IsOnLink(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var dest = new byte[28]; // SOCKADDR_INET
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            BitConverter.GetBytes((ushort)2).CopyTo(dest, 0);
            ip.GetAddressBytes().CopyTo(dest, 4);
        }
        else
        {
            BitConverter.GetBytes((ushort)23).CopyTo(dest, 0);
            ip.GetAddressBytes().CopyTo(dest, 8);
        }
        var row = new byte[256];  // MIB_IPFORWARD_ROW2 (104바이트)보다 넉넉히
        var source = new byte[28];
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, dest, 0, row, source) != 0) return true;
        // NextHop(오프셋 44)이 비어 있으면 게이트웨이 없이 바로 닿는 경로다.
        ushort family = BitConverter.ToUInt16(row, 44);
        var hop = family == 2 ? row.AsSpan(48, 4) : row.AsSpan(52, 16);
        return !hop.ContainsAnyExcept((byte)0);
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress, byte[] destinationAddress,
        uint addressSortOptions, byte[] bestRoute, byte[] bestSourceAddress);

    /// <summary>주소만 보고 인터넷 주소인지. 이 PC·사설망·링크 로컬·CGNAT·멀티캐스트·예약 대역은 아니다(IPv6에 담긴 IPv4도 본다).</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||   // CGNAT 100.64/10
                     (b[0] == 169 && b[1] == 254) ||                  // 링크 로컬
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) ||
                     (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||   // 벤치마크 198.18/15
                     (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var v6 = ip.GetAddressBytes();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
            ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
        // 2000::/3 밖은 쓰지 않는다. 안에 IPv4를 담는 6to4(2002::/16)·Teredo(2001:0::/32)·NAT64(64:ff9b::/96)는 막는다.
        if ((v6[0] & 0xE0) != 0x20) return false;
        if (v6[0] == 0x20 && v6[1] == 0x02) return false;
        if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0 && v6[3] == 0) return false;
        if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8) return false; // 문서용
        return true;
    }
}

/// <summary>앱이 독점으로 쥐는 루프백 소켓 자리: 127.255.255.1~32 중 열리는 곳, 빈 포트.</summary>
internal static class Loopback
{
    public static Socket Listen()
    {
        for (int n = 1; n <= 32; n++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Parse($"127.255.255.{n}"), 0));
                socket.Listen(64);
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }
        throw new IOException("카드의 네트워크를 다룰 자리(127.255.255.1~32)를 열지 못했어요.");
    }
}
