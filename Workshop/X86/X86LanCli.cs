using System;
using System.Linq;
using System.Threading;
using NesEmulator.X86;

namespace BrokenNes.Workshop.X86;

/// <summary><c>--x86lan</c>: self-tests for the x86 LAN (X86-02): the in-process hub, the UDP host / join relay on 127.0.0.1 (three members), the
/// LAN + NAT cable pair and the MAC option. Exit code 0 when every check passes.</summary>
internal static class X86LanCli
{
    private static int _fail, _pass;
    private static void Check(bool ok, string what) { if (ok) _pass++; else { _fail++; Console.WriteLine("FAIL " + what); } }

    private static byte[] Frame(byte src, byte dst, int len = 60)
    {
        var f = new byte[len]; for (int i = 0; i < 6; i++) f[i] = dst == 0xff ? (byte)0xff : (byte)(i == 5 ? dst : 0x52 + (i == 1 ? 2 : 0) - (i > 1 ? 0x52 : 0));
        f[6] = 0x52; f[7] = 0x54; f[11] = src; f[12] = 0x81; f[13] = 0x37;   // IPX ethertype
        for (int i = 14; i < len; i++) f[i] = (byte)(i * src);
        return f;
    }

    private static byte[]? Wait(INetLink l, int ms = 2000) { var t = Environment.TickCount64; while (Environment.TickCount64 - t < ms) { var f = l.Receive(); if (f != null) return f; Thread.Sleep(5); } return null; }

    public static int Run(string[] args)
    {
        var hub = new PcLanHub(); var a = hub.Connect(); var b = hub.Connect(); var c = hub.Connect();
        var f1 = Frame(1, 0xff); a.Send(f1);
        Check(b.Receive() is { } r1 && r1.SequenceEqual(f1) && c.Receive() != null && a.Receive() == null, "hub: a broadcast reaches every other port, not the sender");
        Check(PcMac.Parse("7")[5] == 7 && PcMac.Parse("52:54:00:aa:bb:cc")[3] == 0xaa, "MAC option");
        int port = 27000 + Environment.ProcessId % 2000;
        using var host = PcLanUdp.Host(port);
        using var j1 = PcLanUdp.Join("127.0.0.1", port);
        using var j2 = PcLanUdp.Join("127.0.0.1", port);
        var t0 = Environment.TickCount64; while (host.Members < 2 && Environment.TickCount64 - t0 < 3000) Thread.Sleep(10);
        Check(host.Members == 2, $"UDP host hears both members ({host.Members})");
        var f2 = Frame(2, 0xff, 1514); j1.Send(f2);
        Check(Wait(host) is { } h2 && h2.SequenceEqual(f2), "UDP: member -> host's card");
        Check(Wait(j2) is { } k2 && k2.SequenceEqual(f2), "UDP: member -> other member (relayed)");
        Check(Wait(j1, 200) == null, "UDP: a member does not hear itself");
        var f3 = Frame(3, 2); host.Send(f3);
        Check(Wait(j1) is { } k3 && k3.SequenceEqual(f3) && Wait(j2) != null, "UDP: host's card -> both members");
        // a burst like an IPX game turn: 200 frames in order
        for (int i = 0; i < 200; i++) { var f = Frame(4, 0xff, 100); f[20] = (byte)i; j2.Send(f); }
        int got = 0; bool ordered = true; var t1 = Environment.TickCount64;
        while (got < 200 && Environment.TickCount64 - t1 < 3000) { var f = j1.Receive(); if (f == null) { Thread.Sleep(1); continue; } if (f[20] != (byte)got) ordered = false; got++; }
        Check(got == 200 && ordered, $"UDP: 200-frame burst arrives in order ({got})");
        var pair = new PcLinkPair(a, new PcLanHub().Connect());
        pair.Send(Frame(5, 0xff)); Check(b.Receive() != null, "LAN + NAT pair: a frame goes to the LAN");
        Console.WriteLine($"x86lan: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
