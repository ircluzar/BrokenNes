// The x86 PC's LAN (X86-02, StarCraft on Win98): a virtual Ethernet hub that joins the NE2000 cards of several emulated PCs, so IPX (and any other
// protocol) works between them as on a real LAN party. In one process the hub is a plain object (PcLanHub); between processes or PCs, one instance
// HOSTS the hub on a UDP port and the others JOIN it (PcLanUdp). Frames are raw Ethernet (no FCS), one per datagram, prefixed with "BNL1".
// Hosting binds 127.0.0.1 unless an address is given: nothing listens on the network unless the user asks. Written for BrokenNes.
//
// Every card on the hub needs its own MAC address (IPX node numbers are the MAC): give each PC its own BROKENNES_X86_MAC and COLD BOOT it, because
// a running Windows keeps the address its driver read from the card's PROM at boot (a state loaded on two PCs carries the same address twice).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace NesEmulator.X86;

/// <summary>An in-process Ethernet hub: every frame a port sends goes to every other port (the cards filter by address, as on a real hub).</summary>
public sealed class PcLanHub
{
    private readonly List<Port> _ports = new();
    private readonly object _lock = new();
    public long Frames;

    public sealed class Port : INetLink
    {
        private readonly PcLanHub _hub;
        internal readonly ConcurrentQueue<byte[]> Inbox = new();
        internal Port(PcLanHub hub) { _hub = hub; }
        public void Send(byte[] frame) => _hub.Forward(this, frame);
        public byte[]? Receive() => Inbox.TryDequeue(out var f) ? f : null;
        /// <summary>Frames waiting for this port's card (a card that is not polled must not grow the queue without end).</summary>
        public int Pending => Inbox.Count;
        public void Disconnect() { lock (_hub._lock) _hub._ports.Remove(this); }
    }

    public Port Connect() { var p = new Port(this); lock (_lock) _ports.Add(p); return p; }

    internal void Forward(Port? from, byte[] frame)
    {
        if (frame.Length < 14) return;
        Port[] ports; lock (_lock) ports = _ports.ToArray();
        Frames++;
        foreach (var p in ports) if (p != from && p.Inbox.Count < 512) p.Inbox.Enqueue(frame);
        FrameOut?.Invoke(from, frame);
    }

    /// <summary>The UDP hub's tap: frames from local ports also go to the remote members.</summary>
    internal Action<Port?, byte[]>? FrameOut;
}

/// <summary>
/// The hub between processes: <see cref="Host"/> binds a UDP port and relays every frame to every other member (it is also a member itself, through an
/// in-process port); <see cref="Join"/> connects to such a host. Members say hello every second; one that is silent for 15 s is dropped.
/// </summary>
public sealed class PcLanUdp : INetLink, IDisposable
{
    private static readonly byte[] Magic = "BNL1"u8.ToArray();
    private readonly UdpClient _udp;
    private readonly IPEndPoint? _hostEp;                        // joining: the host
    private readonly PcLanHub? _hub;                             // hosting: the relay
    private readonly PcLanHub.Port? _local;                      // hosting: this PC's own port on the relay
    private readonly ConcurrentDictionary<IPEndPoint, long> _members = new();
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly Thread _rx;
    private readonly System.Threading.Timer _hello;
    private volatile bool _closed;
    public long Sent, Received;
    public string Description { get; }

    private PcLanUdp(UdpClient udp, IPEndPoint? hostEp, bool host, string desc)
    {
        _udp = udp; _hostEp = hostEp; Description = desc;
        if (host)
        {
            _hub = new PcLanHub(); _local = _hub.Connect();
            _hub.FrameOut = (from, f) => { var pkt = Packet(f); foreach (var ep in _members.Keys) Try(() => _udp.Send(pkt, pkt.Length, ep)); };
        }
        _rx = new Thread(RxLoop) { IsBackground = true, Name = "x86 LAN" }; _rx.Start();
        _hello = new System.Threading.Timer(_ => Hello(), null, 0, 1000);
    }

    /// <summary>Hosts the hub on <paramref name="port"/> (127.0.0.1 unless <paramref name="bind"/> names another local address, e.g. 0.0.0.0 for the LAN).</summary>
    public static PcLanUdp Host(int port, string? bind = null)
    {
        var ep = new IPEndPoint(bind == null ? IPAddress.Loopback : IPAddress.Parse(bind), port);
        var u = new UdpClient(ep);
        IgnoreConnReset(u);
        return new PcLanUdp(u, null, true, $"hosting the LAN on {ep}");
    }

    public static PcLanUdp Join(string host, int port)
    {
        var addr = IPAddress.TryParse(host, out var a) ? a : Dns.GetHostAddresses(host)[0];
        var ep = new IPEndPoint(addr, port);
        var u = new UdpClient(new IPEndPoint(addr.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        IgnoreConnReset(u);
        return new PcLanUdp(u, ep, false, $"joined the LAN at {ep}");
    }

    /// <summary>"host:PORT", "host:ADDR:PORT" or "join:HOST:PORT" (BROKENNES_X86_LAN).</summary>
    public static PcLanUdp FromSpec(string spec)
    {
        var p = spec.Split(':');
        if (p[0].Equals("host", StringComparison.OrdinalIgnoreCase)) return p.Length == 2 ? Host(int.Parse(p[1])) : Host(int.Parse(p[2]), p[1]);
        if (p[0].Equals("join", StringComparison.OrdinalIgnoreCase) && p.Length == 3) return Join(p[1], int.Parse(p[2]));
        throw new ArgumentException($"x86 LAN '{spec}': use host:PORT, host:ADDRESS:PORT or join:HOST:PORT");
    }

    private static void IgnoreConnReset(UdpClient u)
    {
        // Windows reports an ICMP port-unreachable as a reset on the next receive: a member that went away must not stop the hub
        if (OperatingSystem.IsWindows()) try { u.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
    }

    private static byte[] Packet(byte[] f) { var b = new byte[4 + f.Length]; Magic.CopyTo(b, 0); f.CopyTo(b, 4); return b; }
    private static void Try(Action a) { try { a(); } catch { } }

    private void Hello()
    {
        if (_closed) return;
        if (_hostEp != null) Try(() => _udp.Send(Magic, 4, _hostEp));
        long now = Environment.TickCount64;
        foreach (var kv in _members) if (now - kv.Value > 15000) _members.TryRemove(kv.Key, out _);
    }

    private void RxLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_closed)
        {
            byte[] b;
            try { b = _udp.Receive(ref any); } catch { if (_closed) return; continue; }
            if (b.Length < 4 || b[0] != Magic[0] || b[1] != Magic[1] || b[2] != Magic[2] || b[3] != Magic[3]) continue;
            if (_hub != null)
            {
                var from = new IPEndPoint(any.Address, any.Port);
                _members[from] = Environment.TickCount64;
                if (b.Length < 4 + 14) continue;                 // a hello
                var f = b[4..]; Received++;
                if (_inbox.Count < 512) _inbox.Enqueue(f);           // to this PC's card
                var pkt = b;
                foreach (var ep in _members.Keys) if (!ep.Equals(from)) Try(() => _udp.Send(pkt, pkt.Length, ep));   // and to the other members
            }
            else
            {
                if (b.Length < 4 + 14) continue;
                Received++;
                if (_inbox.Count < 512) _inbox.Enqueue(b[4..]);
            }
        }
    }

    /// <summary>Members the host has heard from (hosting only).</summary>
    public int Members => _members.Count;

    public void Send(byte[] frame)
    {
        if (_closed || frame.Length < 14) return;
        Sent++;
        if (_hub != null) _hub.Forward(_local, frame);           // to the remote members (FrameOut) and any in-process ports
        else Try(() => _udp.Send(Packet(frame), frame.Length + 4, _hostEp!));
    }

    public byte[]? Receive()
    {
        if (_inbox.TryDequeue(out var f)) return f;
        return _local?.Receive();                                // hosting: frames from other in-process ports
    }

    public void Dispose() { _closed = true; _hello.Dispose(); try { _udp.Close(); } catch { } }
}

/// <summary>One cable to two places: the LAN and the built-in NAT (the NAT only answers what is meant for it; every frame goes to both).</summary>
public sealed class PcLinkPair : INetLink
{
    private readonly INetLink _a, _b; private bool _turn;
    public PcLinkPair(INetLink a, INetLink b) { _a = a; _b = b; }
    public void Send(byte[] frame) { _a.Send(frame); _b.Send(frame); }
    public byte[]? Receive() { _turn = !_turn; return (_turn ? _a.Receive() : _b.Receive()) ?? (_turn ? _b.Receive() : _a.Receive()); }
}

/// <summary>The card's MAC address for this PC: BROKENNES_X86_MAC ("52:54:00:12:34:57", or a number 0-255 for the last byte).</summary>
public static class PcMac
{
    public static byte[] Parse(string spec)
    {
        var mac = new byte[] { 0x52, 0x54, 0x00, 0x12, 0x34, 0x56 };
        if (int.TryParse(spec, out int n) && n is >= 0 and <= 255) { mac[5] = (byte)n; return mac; }
        var p = spec.Split(':', '-');
        if (p.Length != 6) throw new ArgumentException($"x86 MAC '{spec}': six hex bytes (52:54:00:12:34:57) or a number 0-255");
        for (int i = 0; i < 6; i++) mac[i] = Convert.ToByte(p[i], 16);
        if ((mac[0] & 1) != 0) throw new ArgumentException($"x86 MAC '{spec}' is a multicast address");
        return mac;
    }
}
