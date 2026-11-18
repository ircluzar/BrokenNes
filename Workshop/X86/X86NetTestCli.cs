// --x86nettest [--online] [--url HOST/PATH]: the x86 PC's network card + built-in NAT without a guest OS. Written for BrokenNes.
// Offline: drives the RTL8029 like an NE2000 driver (reset, PROM read, ring setup, transmit) against the NAT: ARP, ICMP echo, DHCP, and a
// save state of the card mid-ring that must load back byte for byte. --online also resolves a name through the NAT's DNS and fetches a page
// through the HTTPS bridge (only the public page given, google.com by default).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

public static class X86NetTestCli
{
    private sealed class Drv
    {
        public readonly Rtl8029 Card = new();
        public const byte Start = 0x46, Stop = 0x80, Tx = 0x40;
        public byte Next = Start + 1;
        public Drv(INetLink link)
        {
            Card.Link = link;
            Card.In(0x1f, false);                                   // reset
            Card.Out(0, 0x21, false);                               // page 0, stop, abort DMA
            Card.Out(0x0e, 0x49, false);                            // DCR: word transfers
            Card.Out(0x0a, 0, false); Card.Out(0x0b, 0, false);
            Card.Out(0x0c, 0x04, false);                            // RCR: broadcasts
            Card.Out(0x0d, 0x00, false);
            Card.Out(0x01, Start, false); Card.Out(0x02, Stop, false); Card.Out(0x03, Start, false);
            Card.Out(0, 0x61, false);                               // page 1
            for (int i = 0; i < 6; i++) Card.Out(1 + i, Card.Mac[i], false);
            Card.Out(7, Next, false);
            Card.Out(0, 0x22, false);                               // page 0, start
            Card.Out(0x07, 0xff, false); Card.Out(0x0f, 0x3f, false);
        }
        public byte[] ReadMem(int addr, int n)
        {
            Card.Out(0x08, addr & 0xff, false); Card.Out(0x09, addr >> 8, false);
            Card.Out(0x0a, n & 0xff, false); Card.Out(0x0b, n >> 8, false);
            Card.Out(0, 0x0a, false);
            var r = new byte[n];
            for (int i = 0; i < n; i += 2) { int w = Card.In(0x10, true); r[i] = (byte)w; if (i + 1 < n) r[i + 1] = (byte)(w >> 8); }
            Card.Out(0x07, 0x40, false);
            return r;
        }
        public void Send(byte[] f)
        {
            int n = (f.Length + 1) & ~1;
            Card.Out(0x08, 0, false); Card.Out(0x09, Tx, false);
            Card.Out(0x0a, n & 0xff, false); Card.Out(0x0b, n >> 8, false);
            Card.Out(0, 0x12, false);
            for (int i = 0; i < n; i += 2) Card.Out(0x10, f[i] | (i + 1 < f.Length ? f[i + 1] << 8 : 0), true);
            Card.Out(0x07, 0x40, false);
            Card.Out(0x04, Tx, false); Card.Out(0x05, f.Length & 0xff, false); Card.Out(0x06, f.Length >> 8, false);
            Card.Out(0, 0x26, false);
        }
        public byte[]? Recv()
        {
            Card.Poll();
            Card.Out(0, 0x62, false); int curr = Card.In(7, false); Card.Out(0, 0x22, false);
            if (curr == Next) return null;
            var h = ReadMem(Next << 8, 4);
            int len = h[2] | h[3] << 8;
            var all = new List<byte>();
            int a = (Next << 8) + 4, left = len - 4;
            while (left > 0) { int chunk = Math.Min(left, (Stop << 8) - a); all.AddRange(ReadMem(a, (chunk + 1) & ~1).Take(chunk)); left -= chunk; a = Start << 8; }
            Next = h[1];
            Card.Out(0x03, Next == Start ? Stop - 1 : Next - 1, false);
            Card.Out(0x07, 0x01, false);
            return all.ToArray();
        }
    }

    private static readonly byte[] Me = { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
    private static ushort Sum(byte[] b, int o, int n, uint s = 0) { for (int i = 0; i + 1 < n; i += 2) s += (uint)(b[o + i] << 8 | b[o + i + 1]); if ((n & 1) != 0) s += (uint)(b[o + n - 1] << 8); while (s >> 16 != 0) s = (s & 0xffff) + (s >> 16); return (ushort)~s; }
    private static byte[] Eth(byte[] dst, int type, byte[] p) { var f = new byte[14 + p.Length]; dst.CopyTo(f, 0); Me.CopyTo(f, 6); f[12] = (byte)(type >> 8); f[13] = (byte)type; p.CopyTo(f, 14); return f; }
    private static byte[] Ip(byte[] src, byte[] dst, int proto, byte[] pl)
    {
        var p = new byte[20 + pl.Length]; p[0] = 0x45; p[2] = (byte)(p.Length >> 8); p[3] = (byte)p.Length; p[8] = 64; p[9] = (byte)proto;
        src.CopyTo(p, 12); dst.CopyTo(p, 16); ushort c = Sum(p, 0, 20); p[10] = (byte)(c >> 8); p[11] = (byte)c; pl.CopyTo(p, 20); return p;
    }
    private static uint Ps(byte[] s, byte[] d, int proto, int len) { uint a = 0; for (int i = 0; i < 4; i += 2) { a += (uint)(s[i] << 8 | s[i + 1]); a += (uint)(d[i] << 8 | d[i + 1]); } return a + (uint)proto + (uint)len; }
    private static byte[] Udp(byte[] s, byte[] d, int sp, int dp, byte[] b)
    {
        var u = new byte[8 + b.Length]; u[0] = (byte)(sp >> 8); u[1] = (byte)sp; u[2] = (byte)(dp >> 8); u[3] = (byte)dp; u[4] = (byte)(u.Length >> 8); u[5] = (byte)u.Length; b.CopyTo(u, 8);
        ushort c = Sum(u, 0, u.Length, Ps(s, d, 17, u.Length)); u[6] = (byte)(c >> 8); u[7] = (byte)c; return Ip(s, d, 17, u);
    }
    private static byte[] Tcp(byte[] s, byte[] d, int sp, int dp, uint seq, uint ack, int fl, byte[] data)
    {
        var t = new byte[20 + data.Length]; t[0] = (byte)(sp >> 8); t[1] = (byte)sp; t[2] = (byte)(dp >> 8); t[3] = (byte)dp;
        void P32(int o, uint v) { t[o] = (byte)(v >> 24); t[o + 1] = (byte)(v >> 16); t[o + 2] = (byte)(v >> 8); t[o + 3] = (byte)v; }
        P32(4, seq); P32(8, ack); t[12] = 0x50; t[13] = (byte)fl; t[14] = 0x20; t[15] = 0x00; data.CopyTo(t, 20);
        ushort c = Sum(t, 0, t.Length, Ps(s, d, 6, t.Length)); t[16] = (byte)(c >> 8); t[17] = (byte)c; return Ip(s, d, 6, t);
    }
    private static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    public static int Run(string[] args)
    {
        var nat = new PcNat { Log = l => Console.WriteLine("  nat: " + l) };
        var d = new Drv(nat);
        bool ok = true;
        void Check(string what, bool c) { Console.WriteLine($"x86nettest: {(c ? "ok  " : "FAIL")} {what}"); ok &= c; }
        byte[]? Wait(Func<byte[], bool> want, int ms = 3000)
        {
            var t0 = DateTime.UtcNow;
            while ((DateTime.UtcNow - t0).TotalMilliseconds < ms) { var f = d.Recv(); if (f != null && want(f)) return f; if (f == null) System.Threading.Thread.Sleep(5); }
            return null;
        }
        Check("PROM holds the MAC (word mode)", d.ReadMem(0, 12).Where((b, i) => i % 2 == 0).SequenceEqual(d.Card.Mac));
        byte[] zero = { 0, 0, 0, 0 }, me = { 10, 0, 2, 15 }, gw = { 10, 0, 2, 2 }, dns = { 10, 0, 2, 3 }, bc = { 255, 255, 255, 255 };
        var ff = Enumerable.Repeat((byte)0xff, 6).ToArray();
        // DHCP discover
        var dh = new byte[300]; dh[0] = 1; dh[1] = 1; dh[2] = 6; dh[4] = 0x12; dh[5] = 0x34; Me.CopyTo(dh, 28);
        dh[236] = 0x63; dh[237] = 0x82; dh[238] = 0x53; dh[239] = 0x63; dh[240] = 53; dh[241] = 1; dh[242] = 1; dh[243] = 255;
        d.Send(Eth(ff, 0x0800, Udp(zero, bc, 68, 67, dh)));
        var off = Wait(f => f.Length > 42 && f[23] == 17 && f[36] == 0 && f[37] == 68);
        Check("DHCP offer of 10.0.2.15", off != null && off.Skip(42 + 16).Take(4).SequenceEqual(me));
        // ARP for the gateway
        var arp = new byte[28]; arp[1] = 1; arp[2] = 8; arp[4] = 6; arp[5] = 4; arp[7] = 1; Me.CopyTo(arp, 8); me.CopyTo(arp, 14); gw.CopyTo(arp, 24);
        d.Send(Eth(ff, 0x0806, arp));
        var ar = Wait(f => f[12] == 8 && f[13] == 6);
        Check("ARP reply for 10.0.2.2", ar != null && ar.Skip(22).Take(6).SequenceEqual(PcNat.GatewayMac));
        // a state saved with frames in the ring loads back the same
        var ping = new byte[40]; ping[0] = 8; for (int i = 8; i < 40; i++) ping[i] = (byte)i; ushort pc = Sum(ping, 0, 40); ping[2] = (byte)(pc >> 8); ping[3] = (byte)pc;
        d.Send(Eth(PcNat.GatewayMac, 0x0800, Ip(me, gw, 1, ping)));
        d.Card.Poll();                    // the echo reply is now in the ring
        var ms1 = new MemoryStream(); d.Card.Save(new BinaryWriter(ms1));
        var copy = new Rtl8029(); copy.Load(new BinaryReader(new MemoryStream(ms1.ToArray())));
        var ms2 = new MemoryStream(); copy.Save(new BinaryWriter(ms2));
        Check("card state round-trips byte for byte", ms1.ToArray().SequenceEqual(ms2.ToArray()));
        var echo = Wait(f => f[12] == 8 && f[23] == 1);
        if (echo != null) Console.WriteLine("  echo: " + BitConverter.ToString(echo));
        Check("ICMP echo reply from the gateway", echo != null && echo[34] == 0 && echo.Skip(42).Take(32).SequenceEqual(ping.Skip(8)));

        if (args.Contains("--online"))
        {
            string url = args.SkipWhile(a => a != "--url").Skip(1).FirstOrDefault() ?? "www.google.com/";
            string host = url.Split('/')[0], path = "/" + string.Join("/", url.Split('/').Skip(1));
            var q = new List<byte> { 0xab, 0xcd, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var part in host.Split('.')) { q.Add((byte)part.Length); q.AddRange(Encoding.ASCII.GetBytes(part)); }
            q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            d.Send(Eth(PcNat.GatewayMac, 0x0800, Udp(me, dns, 1025, 53, q.ToArray())));
            var dr = Wait(f => f[23] == 17 && f[34] == 0 && f[35] == 53, 8000);
            Check($"DNS answer for {host}", dr != null && dr[42 + 7] > 0);
            byte[] rip = dr != null && dr[42 + 7] > 0 ? dr.Skip(dr.Length - 4).Take(4).ToArray() : new byte[] { 1, 2, 3, 4 };
            uint seq = 1000;
            d.Send(Eth(PcNat.GatewayMac, 0x0800, Tcp(me, rip, 1100, 80, seq, 0, 0x02, Array.Empty<byte>())));
            var sa = Wait(f => f[23] == 6 && (f[47] & 0x12) == 0x12);
            Check("TCP SYN-ACK on port 80", sa != null);
            if (sa != null)
            {
                uint rs = U32(sa, 38) + 1; seq++;
                var req = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.0\r\nHost: {host}\r\nUser-Agent: Mozilla/4.0 (compatible; MSIE 5.0; Windows 98)\r\n\r\n");
                d.Send(Eth(PcNat.GatewayMac, 0x0800, Tcp(me, rip, 1100, 80, seq, rs, 0x18, req))); seq += (uint)req.Length;
                var body = new List<byte>(); bool fin = false; var t0 = DateTime.UtcNow;
                while (!fin && (DateTime.UtcNow - t0).TotalSeconds < 30)
                {
                    var f = d.Recv(); if (f == null) { System.Threading.Thread.Sleep(5); continue; }
                    if (f[23] != 6) continue;
                    int ihl = (f[14] & 15) * 4, to = 14 + ihl, dl = ((f[16] << 8) | f[17]) - ihl - (f[to + 12] >> 4) * 4;
                    if (U32(f, to + 4) == rs) { body.AddRange(f.Skip(to + (f[to + 12] >> 4) * 4).Take(dl)); rs += (uint)dl; if ((f[to + 13] & 1) != 0) { rs++; fin = true; } }
                    d.Send(Eth(PcNat.GatewayMac, 0x0800, Tcp(me, rip, 1100, 80, seq, rs, 0x10, Array.Empty<byte>())));
                }
                var text = Encoding.Latin1.GetString(body.ToArray());
                Console.WriteLine($"x86nettest: got {body.Count} bytes, status line: {text.Split("\r\n")[0]}");
                Check("bridged HTTP answer, closed with FIN", fin && text.StartsWith("HTTP/1.0 "));
                int hi = text.IndexOf("https://"); if (hi >= 0) Console.WriteLine("  https at: " + text.Substring(Math.Max(0, hi - 80), 160));
                Check("no https:// left in the page", !text.Contains("https://"));
            }
        }
        Console.WriteLine(ok ? "x86nettest: PASS" : "x86nettest: FAIL");
        return ok ? 0 : 1;
    }
}
