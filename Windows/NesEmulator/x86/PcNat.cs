// The x86 PC's built-in network: a user-mode NAT (slirp style) on the other end of the network card's cable. Written for BrokenNes.
// The guest lives on 10.0.2.0/24: it is 10.0.2.15, the gateway is 10.0.2.2, the DNS server 10.0.2.3; DHCP hands that out. Nothing listens on
// the host: the NAT only makes outbound connections for the guest (TCP through host sockets, DNS through the host's resolver).
//
// The HTTPS bridge: an old browser cannot speak modern TLS, so plain HTTP (TCP port 80) from the guest is answered by the emulator itself.
// It reads the request, fetches the page over HTTPS from the host (plain HTTP if the site has no HTTPS), rewrites https:// links to http://
// so the next click comes back through the bridge, and sends an HTTP/1.0 answer. Other TCP ports are forwarded as they are.
//
// Everything the guest sees happens on the emulation thread (Send / Receive); host I/O runs on tasks that post their results back.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NesEmulator.X86;

public sealed class PcNat : INetLink, IDisposable
{
    public static readonly byte[] GatewayMac = { 0x52, 0x55, 0x0a, 0x00, 0x02, 0x02 };
    public static readonly byte[] Net = { 10, 0, 2, 0 };
    public const byte GatewayHost = 2, DnsHost = 3, GuestHost = 15;
    private static readonly byte[] Broadcast = { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };

    private readonly ConcurrentQueue<byte[]> _toGuest = new();
    private readonly ConcurrentQueue<Action> _posted = new();     // host results, run on the emulation thread
    private readonly Dictionary<(ushort gport, uint rip, ushort rport), TcpConn> _tcp = new();
    private byte[] _guestMac = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
    private static readonly HttpClient Http = MakeClient();
    private ushort _ipId;
    /// <summary>Log lines (DHCP, DNS, bridged URLs) for the tools; never contains anything but what the guest asked for.</summary>
    public Action<string>? Log;
    public bool HttpBridge = true;

    private static HttpClient MakeClient()
    {
        var h = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static uint Ip(byte host) => (uint)(10 << 24 | 0 << 16 | 2 << 8 | host);
    private static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static ushort U16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);
    private static void P32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static void P16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

    // ================= INetLink =================

    public byte[]? Receive()
    {
        while (_posted.TryDequeue(out var a)) a();
        foreach (var c in _tcp.Values.ToList()) c.Pump();
        return _toGuest.TryDequeue(out var f) ? f : null;
    }

    public void Send(byte[] f)
    {
        if (f.Length < 14) return;
        Array.Copy(f, 6, _guestMac, 0, 6);
        int type = U16(f, 12);
        if (type == 0x0806) Arp(f);
        else if (type == 0x0800) Ipv4(f);
    }

    public void Dispose() { foreach (var c in _tcp.Values) c.Close(); _tcp.Clear(); }

    private void Eth(byte[] dst, int type, byte[] payload)
    {
        var f = new byte[14 + payload.Length];
        Array.Copy(dst, 0, f, 0, 6); Array.Copy(GatewayMac, 0, f, 6, 6); P16(f, 12, type);
        Array.Copy(payload, 0, f, 14, payload.Length);
        _toGuest.Enqueue(f);
    }

    // ================= ARP =================

    private void Arp(byte[] f)
    {
        if (f.Length < 42 || U16(f, 20) != 1) return;     // requests only
        uint target = U32(f, 38);
        if ((target & 0xffffff00) != Ip(0) || (target & 0xff) == GuestHost || (target & 0xff) == 0) return;
        var a = new byte[28];
        P16(a, 0, 1); P16(a, 2, 0x0800); a[4] = 6; a[5] = 4; P16(a, 6, 2);
        Array.Copy(GatewayMac, 0, a, 8, 6); P32(a, 14, target);
        Array.Copy(f, 22, a, 18, 6); Array.Copy(f, 28, a, 24, 4);
        Eth(Sub(f, 6, 6), 0x0806, a);
    }

    private static byte[] Sub(byte[] b, int o, int n) { var r = new byte[n]; Array.Copy(b, o, r, 0, n); return r; }

    // ================= IPv4 =================

    private static ushort Checksum(byte[] b, int o, int n, uint sum = 0)
    {
        for (int i = 0; i + 1 < n; i += 2) sum += (uint)(b[o + i] << 8 | b[o + i + 1]);
        if ((n & 1) != 0) sum += (uint)(b[o + n - 1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    private void Ipv4(byte[] f)
    {
        int ihl = (f[14] & 0x0f) * 4, total = U16(f, 16);
        if (f[14] >> 4 != 4 || total < ihl || 14 + total > f.Length) return;
        if ((U16(f, 20) & 0x3fff) != 0) return;            // fragments: not handled
        int proto = f[23];
        uint src = U32(f, 26), dst = U32(f, 30);
        var data = Sub(f, 14 + ihl, total - ihl);
        if (proto == 17) Udp(src, dst, data);
        else if (proto == 6) Tcp(src, dst, data);
        else if (proto == 1) Icmp(src, dst, data);
    }

    private void SendIp(uint src, uint dst, int proto, byte[] payload)
    {
        var p = new byte[20 + payload.Length];
        p[0] = 0x45; P16(p, 2, p.Length); P16(p, 4, _ipId++); p[8] = 64; p[9] = (byte)proto;
        P32(p, 12, src); P32(p, 16, dst);
        P16(p, 10, Checksum(p, 0, 20));
        Array.Copy(payload, 0, p, 20, payload.Length);
        Eth(dst == 0xffffffff ? Broadcast : _guestMac, 0x0800, p);
    }

    private static uint Pseudo(uint src, uint dst, int proto, int len) =>
        (src >> 16) + (src & 0xffff) + (dst >> 16) + (dst & 0xffff) + (uint)proto + (uint)len;

    private void Icmp(uint src, uint dst, byte[] d)
    {
        if (d.Length < 8 || d[0] != 8 || (dst & 0xffffff00) != Ip(0)) return;   // echo requests to our own addresses
        var r = (byte[])d.Clone(); r[0] = 0; r[2] = r[3] = 0;
        P16(r, 2, Checksum(r, 0, r.Length));
        SendIp(dst, src, 1, r);
    }

    // ================= UDP: DHCP and DNS =================

    private void Udp(uint src, uint dst, byte[] d)
    {
        if (d.Length < 8) return;
        int sport = U16(d, 0), dport = U16(d, 2);
        var body = Sub(d, 8, Math.Min(U16(d, 4), d.Length) - 8);
        if (dport == 67) Dhcp(body);
        else if (dport == 53 && dst == Ip(DnsHost)) Dns(src, sport, body);
    }

    private void SendUdp(uint src, int sport, uint dst, int dport, byte[] body)
    {
        var u = new byte[8 + body.Length];
        P16(u, 0, sport); P16(u, 2, dport); P16(u, 4, u.Length);
        Array.Copy(body, 0, u, 8, body.Length);
        ushort c = Checksum(u, 0, u.Length, Pseudo(src, dst, 17, u.Length));
        P16(u, 6, c == 0 ? 0xffff : c);
        SendIp(src, dst, 17, u);
    }

    private void Dhcp(byte[] b)
    {
        if (b.Length < 240 || b[0] != 1 || U32(b, 236) != 0x63825363) return;
        int type = 0;
        for (int i = 240; i < b.Length && b[i] != 255;) { if (b[i] == 0) { i++; continue; } if (b[i] == 53) type = b[i + 2]; i += 2 + b[i + 1]; }
        if (type is not (1 or 3)) return;                 // DISCOVER -> OFFER, REQUEST -> ACK
        var r = new byte[300];
        r[0] = 2; r[1] = 1; r[2] = 6;
        Array.Copy(b, 4, r, 4, 4);                         // xid
        Array.Copy(b, 10, r, 10, 2);                       // flags
        P32(r, 16, Ip(GuestHost)); P32(r, 20, Ip(GatewayHost));
        Array.Copy(b, 28, r, 28, 16);                      // chaddr
        P32(r, 236, 0x63825363);
        int o = 240;
        void Opt(byte code, params byte[] v) { r[o++] = code; r[o++] = (byte)v.Length; Array.Copy(v, 0, r, o, v.Length); o += v.Length; }
        byte[] IpB(byte h) => new byte[] { 10, 0, 2, h };
        Opt(53, (byte)(type == 1 ? 2 : 5));
        Opt(54, IpB(GatewayHost));
        Opt(51, 0, 1, 0x51, 0x80);                         // 86400 s
        Opt(1, 255, 255, 255, 0);
        Opt(3, IpB(GatewayHost));
        Opt(6, IpB(DnsHost));
        Opt(15, Encoding.ASCII.GetBytes("brokennes"));
        r[o++] = 255;
        Log?.Invoke($"dhcp: {(type == 1 ? "offer" : "ack")} 10.0.2.15");
        SendUdp(Ip(GatewayHost), 67, 0xffffffff, 68, r);
    }

    private void Dns(uint src, int sport, byte[] q)
    {
        if (q.Length < 12 || (q[2] & 0x80) != 0 || U16(q, 4) != 1) return;
        int p = 12; var name = new StringBuilder();
        while (p < q.Length && q[p] != 0)
        {
            int n = q[p]; if (n > 63 || p + 1 + n > q.Length) return;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(q, p + 1, n)); p += 1 + n;
        }
        if (p + 5 > q.Length) return;
        int qtype = U16(q, p + 1), qend = p + 5;
        string host = name.ToString();
        var question = Sub(q, 0, qend);
        void Answer(IPAddress[] addrs, bool nx)
        {
            var a = new List<byte>(question);
            a[2] = (byte)(0x81 | (q[2] & 0x01)); a[3] = (byte)(nx ? 0x83 : 0x80);   // response, RD copied, RA, rcode
            var v4 = addrs.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Take(8).ToArray();
            a[6] = 0; a[7] = (byte)v4.Length; a[8] = a[9] = a[10] = a[11] = 0;
            foreach (var ip in v4) { a.AddRange(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4 }); a.AddRange(ip.GetAddressBytes()); }
            Log?.Invoke($"dns: {host} -> {(nx ? "NXDOMAIN" : string.Join(",", v4.Select(x => x.ToString())))}");
            SendUdp(Ip(DnsHost), 53, src, sport, a.ToArray());
        }
        if (qtype != 1 || host.Length == 0) { Answer(Array.Empty<IPAddress>(), false); return; }
        Task.Run(async () =>
        {
            IPAddress[] r; bool nx = false;
            try { r = await System.Net.Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork); }
            catch { r = Array.Empty<IPAddress>(); nx = true; }
            _posted.Enqueue(() => Answer(r, nx));
        });
    }

    // ================= TCP =================

    private void Tcp(uint src, uint dst, byte[] d)
    {
        if (d.Length < 20) return;
        ushort sport = U16(d, 0), dport = U16(d, 2);
        uint seq = U32(d, 4), ack = U32(d, 8);
        int off = (d[12] >> 4) * 4, flags = d[13];
        ushort win = U16(d, 14);
        if (off < 20 || off > d.Length) return;
        var payload = Sub(d, off, d.Length - off);
        var key = (sport, dst, dport);
        _tcp.TryGetValue(key, out var c);
        if ((flags & 0x04) != 0) { if (c != null) { c.Close(); _tcp.Remove(key); } return; }   // RST
        if ((flags & 0x02) != 0 && (flags & 0x10) == 0)
        {
            if (c != null) { c.Close(); _tcp.Remove(key); }
            c = new TcpConn(this, key, src, seq, win);
            _tcp[key] = c;
            c.Open();
            return;
        }
        if (c == null)
        {
            if ((flags & 0x01) != 0 || payload.Length > 0 || (flags & 0x10) != 0) SendRst(dst, dport, src, sport, ack, seq + (uint)payload.Length + ((flags & 0x01) != 0 ? 1u : 0));
            return;
        }
        c.Segment(seq, ack, flags, win, payload);
        if (c.Dead) _tcp.Remove(key);
    }

    private void SendRst(uint src, int sport, uint dst, int dport, uint seq, uint ack) => SendTcp(src, sport, dst, dport, seq, ack, 0x14, 0, Array.Empty<byte>());

    private void SendTcp(uint src, int sport, uint dst, int dport, uint seq, uint ack, int flags, int win, byte[] data, bool mss = false)
    {
        int hl = mss ? 24 : 20;
        var t = new byte[hl + data.Length];
        P16(t, 0, sport); P16(t, 2, dport); P32(t, 4, seq); P32(t, 8, ack);
        t[12] = (byte)(hl / 4 << 4); t[13] = (byte)flags; P16(t, 14, win);
        if (mss) { t[20] = 2; t[21] = 4; P16(t, 22, 1460); }
        Array.Copy(data, 0, t, hl, data.Length);
        P16(t, 16, Checksum(t, 0, t.Length, Pseudo(src, dst, 6, t.Length)));
        SendIp(src, dst, 6, t);
    }

    /// <summary>One guest TCP connection, terminated here: its bytes go to a host socket, or to the HTTP bridge (port 80).</summary>
    private sealed class TcpConn
    {
        private readonly PcNat _n;
        private readonly (ushort gport, uint rip, ushort rport) _k;
        private readonly uint _guest;
        private uint _rcvNxt, _sndUna, _sndNxt, _iss;
        private int _guestWin;
        private readonly List<byte> _out = new();            // bytes for the guest not yet acknowledged (from _sndUna)
        private readonly List<byte> _in = new();             // bridge: the request so far
        private bool _upClosed, _finSent, _guestFin, _bridgeStarted;
        private Socket? _sock;
        private DateTime _lastProgress = DateTime.UtcNow;
        private readonly ConcurrentQueue<byte[]> _sockIn = new();
        public bool Dead;
        private bool Bridge => _k.rport == 80 && _n.HttpBridge;

        public TcpConn(PcNat n, (ushort, uint, ushort) k, uint guest, uint seq, ushort win)
        {
            _n = n; _k = k; _guest = guest; _rcvNxt = seq + 1; _guestWin = win;
            _iss = (uint)Environment.TickCount * 2654435761u; _sndUna = _sndNxt = _iss;
        }

        private void Out(int flags, uint seq, byte[] data, bool mss = false) =>
            _n.SendTcp(_k.rip, _k.rport, _guest, _k.gport, seq, _rcvNxt, flags, 0xfaf0, data, mss);

        public void Open()
        {
            if (Bridge) { SynAck(); return; }
            var ip = new IPAddress(new[] { (byte)(_k.rip >> 24), (byte)(_k.rip >> 16), (byte)(_k.rip >> 8), (byte)_k.rip });
            if ((_k.rip & 0xffffff00) == Ip(0) || (_k.rip >> 24) == 127) { _n.SendRst(_k.rip, _k.rport, _guest, _k.gport, 0, _rcvNxt); Dead = true; return; }
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _sock = s;
            Task.Run(async () =>
            {
                try { await s.ConnectAsync(ip, _k.rport); }
                catch { _n._posted.Enqueue(() => { if (!Dead) { _n.SendRst(_k.rip, _k.rport, _guest, _k.gport, 0, _rcvNxt); Dead = true; } }); return; }
                _n._posted.Enqueue(() => { if (!Dead) SynAck(); });
                var buf = new byte[16384];
                try
                {
                    while (true)
                    {
                        int r = await s.ReceiveAsync(buf, SocketFlags.None);
                        if (r <= 0) break;
                        _sockIn.Enqueue(buf.AsSpan(0, r).ToArray());
                    }
                }
                catch { }
                _sockIn.Enqueue(Array.Empty<byte>());
            });
        }

        private void SynAck() { Out(0x12, _iss, Array.Empty<byte>(), mss: true); _sndUna = _sndNxt = _iss + 1; _lastProgress = DateTime.UtcNow; }

        public void Segment(uint seq, uint ack, int flags, ushort win, byte[] payload)
        {
            _guestWin = win;
            if ((flags & 0x10) != 0)
            {
                int acked = (int)(ack - _sndUna);
                int inFlight = (int)(_sndNxt - _sndUna);
                if (acked > 0 && acked <= inFlight)
                {
                    int data = Math.Min(acked, _out.Count);
                    _out.RemoveRange(0, data);
                    _sndUna = ack; _lastProgress = DateTime.UtcNow;
                }
                if (_finSent && ack == _sndNxt && _out.Count == 0 && _guestFin) { Close(); return; }
            }
            if (payload.Length > 0 || (flags & 0x01) != 0)
            {
                if (seq == _rcvNxt)
                {
                    _rcvNxt += (uint)payload.Length;
                    if (payload.Length > 0)
                    {
                        if (Bridge) { _in.AddRange(payload); TryBridge(); }
                        else if (_sock != null) try { _sock.Send(payload); } catch { }
                    }
                    if ((flags & 0x01) != 0)
                    {
                        _rcvNxt++; _guestFin = true;
                        if (!Bridge) try { _sock?.Shutdown(SocketShutdown.Send); } catch { }
                    }
                }
                Out(0x10, _sndNxt, Array.Empty<byte>());     // ACK (a duplicate one for out-of-order data)
                if (_guestFin && _finSent && _out.Count == 0 && _sndNxt == _sndUna) Close();
            }
            Pump();
        }

        /// <summary>Sends what the guest's window allows; retransmits after a stall; closes when everything went.</summary>
        public void Pump()
        {
            if (Dead) return;
            while (_sockIn.TryDequeue(out var b)) { if (b.Length == 0) _upClosed = true; else _out.AddRange(b); }
            if (_sndNxt != _sndUna && (DateTime.UtcNow - _lastProgress).TotalSeconds > 1.5)
            {
                _sndNxt = _sndUna; _lastProgress = DateTime.UtcNow;   // go back: resend from the first unacknowledged byte
                if (_finSent) _finSent = false;
            }
            int sent = (int)(_sndNxt - _sndUna);
            if (_finSent && sent > 0) sent--;
            while (sent < _out.Count && sent < Math.Max(_guestWin, 1))
            {
                int n = Math.Min(Math.Min(1460, _out.Count - sent), Math.Max(_guestWin, 1) - sent);
                if (n <= 0) break;
                var seg = _out.GetRange(sent, n).ToArray();
                Out(0x18, _sndNxt, seg);
                _sndNxt += (uint)n; sent += n;
            }
            if (_upClosed && !_finSent && sent == _out.Count)
            {
                Out(0x11, _sndNxt, Array.Empty<byte>());
                _sndNxt++; _finSent = true;
            }
        }

        public void Close()
        {
            Dead = true;
            try { _sock?.Dispose(); } catch { }
        }

        // ================= the HTTP bridge =================

        private void TryBridge()
        {
            if (_bridgeStarted) return;
            var all = _in.ToArray();
            int he = IndexOf(all, "\r\n\r\n"u8.ToArray());
            if (he < 0) return;
            var head = Encoding.Latin1.GetString(all, 0, he);
            var lines = head.Split("\r\n");
            var req = lines[0].Split(' ');
            if (req.Length < 2) return;
            var headers = new List<(string, string)>();
            int clen = 0;
            foreach (var l in lines.Skip(1)) { int i = l.IndexOf(':'); if (i <= 0) continue; var k = l[..i].Trim(); var v = l[(i + 1)..].Trim(); headers.Add((k, v)); if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out clen); }
            if (all.Length < he + 4 + clen) return;
            _bridgeStarted = true;
            var body = Sub(all, he + 4, clen);
            string method = req[0], target = req[1];
            string host = headers.FirstOrDefault(h => h.Item1.Equals("Host", StringComparison.OrdinalIgnoreCase)).Item2 ?? "";
            string path = target;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))   // the guest uses us as its proxy
            {
                var u = new Uri(target); host = u.Authority; path = u.PathAndQuery;
            }
            if (host.EndsWith(":80")) host = host[..^3];
            if (host.Length == 0) { host = $"{_k.rip >> 24}.{(_k.rip >> 16) & 255}.{(_k.rip >> 8) & 255}.{_k.rip & 255}"; }
            _n.Log?.Invoke($"http: {method} http://{host}{path}");
            Task.Run(async () =>
            {
                byte[] resp;
                try { resp = await Fetch(method, host, path, headers, body); }
                catch (Exception e) { resp = Error(502, "The bridge could not fetch " + host + path + ": " + e.Message); }
                _n._posted.Enqueue(() => { if (Dead) return; _out.AddRange(resp); _upClosed = true; Pump(); });
            });
        }

        private static int IndexOf(byte[] a, byte[] p)
        {
            for (int i = 0; i + p.Length <= a.Length; i++) { int k = 0; while (k < p.Length && a[i + k] == p[k]) k++; if (k == p.Length) return i; }
            return -1;
        }

        private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Connection", "Proxy-Connection", "Keep-Alive", "Content-Length", "Transfer-Encoding", "Accept-Encoding", "Upgrade", "TE", "Content-Type" };
        private static readonly HashSet<string> DropOut = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Content-Encoding", "Strict-Transport-Security", "Content-Security-Policy", "Alt-Svc", "Upgrade" };

        private static async Task<byte[]> Fetch(string method, string host, string path, List<(string, string)> headers, byte[] body)
        {
            HttpResponseMessage? r = null; Exception? last = null;
            foreach (var scheme in new[] { "https", "http" })
            {
                var m = new HttpRequestMessage(new HttpMethod(method), $"{scheme}://{host}{path}");
                foreach (var (k, v) in headers) if (!Hop.Contains(k)) m.Headers.TryAddWithoutValidation(k, v.Replace("http://", "https://"));
                if (body.Length > 0 || method is "POST" or "PUT")
                {
                    m.Content = new ByteArrayContent(body);
                    var ct = headers.FirstOrDefault(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Item2;
                    if (ct != null) m.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
                }
                try { r = await Http.SendAsync(m); break; }
                catch (Exception e) { last = e; }
            }
            if (r == null) throw last ?? new Exception("no answer");
            var data = await r.Content.ReadAsByteArrayAsync();
            string ctype = r.Content.Headers.ContentType?.ToString() ?? "";
            if (ctype.StartsWith("text/") || ctype.Contains("javascript") || ctype.Contains("json") || ctype.Contains("xml"))
                data = Downgrade(data);
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.0 {(int)r.StatusCode} {r.ReasonPhrase}\r\n");
            foreach (var h in r.Headers.Concat(r.Content.Headers))
            {
                if (DropOut.Contains(h.Key) || h.Key.StartsWith("Content-Security-Policy", StringComparison.OrdinalIgnoreCase) || h.Key.StartsWith("Report-To", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in h.Value)
                {
                    var val = v;
                    val = val.Replace("https://", "http://");
                    if (h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) val = System.Text.RegularExpressions.Regex.Replace(val, @";\s*(Secure|SameSite=[^;]*|Partitioned)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    sb.Append(h.Key).Append(": ").Append(val).Append("\r\n");
                }
            }
            sb.Append($"Content-Length: {data.Length}\r\nConnection: close\r\n\r\n");
            var head = Encoding.Latin1.GetBytes(sb.ToString());
            var res = new byte[head.Length + data.Length];
            head.CopyTo(res, 0); data.CopyTo(res, head.Length);
            return res;
        }

        /// <summary>https:// -> http:// (also the JSON-escaped form): links lead back through the bridge.</summary>
        private static byte[] Downgrade(byte[] d)
        {
            var s = Encoding.Latin1.GetString(d);
            s = s.Replace("https://", "http://").Replace("https:\\/\\/", "http:\\/\\/").Replace("https%3A%2F%2F", "http%3A%2F%2F");
            return Encoding.Latin1.GetBytes(s);
        }

        private static byte[] Error(int code, string msg)
        {
            var body = Encoding.ASCII.GetBytes($"<html><body><h2>BrokenNes network bridge</h2><p>{WebUtility.HtmlEncode(msg)}</p></body></html>");
            var head = Encoding.ASCII.GetBytes($"HTTP/1.0 {code} Bridge Error\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            return head.Concat(body).ToArray();
        }
    }
}
