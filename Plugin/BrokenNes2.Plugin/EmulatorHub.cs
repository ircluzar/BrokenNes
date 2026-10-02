// The emulators of one FL Studio process, shared by every BrokenNes2 instance in it.
//
// One FL instance of BrokenNes2 is one channel of an emulator, like any other instrument is one voice of its own:
// pulse 1, pulse 2, triangle, noise, or (ROM mode) the whole mix of the game. Instances that choose the same emulator
// share it: its mode, sound chip, CPU/PPU cores, game and picture. FL's DLL is loaded once per process, so a static
// hub is all the coordination needed.
//
//   Emulator = Auto  joins the first auto-created emulator that has the wanted channel free, and only when there is none
//                    starts a new one: the fewest emulators that fit. Channel = Auto takes the first free channel.
//   Emulator = #1..#8 names an emulator explicitly (it is created if it does not exist). This is how a Game Boy chip gets
//                    its own emulator next to the NES one. An explicit emulator never fills up with Auto instances.
//
// All claims and configuration changes happen under one lock; the audio threads only read the volatile results.
using BrokenNes.Fruity;

namespace BrokenNes2;

/// <summary>Where an instance sits: its emulator and channel (-1 when it has none), and why not.</summary>
public sealed record Assignment(Emulator? Emulator, int Channel, string? Problem)
{
    public static readonly Assignment None = new(null, -1, "not attached to an emulator");
    public bool Sounding => Emulator != null && Channel >= 0;
}

public static class EmulatorHub
{
    public const int MaxEmulators = 8;
    internal static readonly object Gate = new();
    private static readonly Emulator?[] slots = new Emulator?[MaxEmulators];
    private static int hostRate = 44100;
    private static readonly List<Bn2Plugin> orphans = new();   // instances that found no emulator at all (all eight in use)
    private static bool retrying;

    /// <summary>The sample rate FL runs at (every instance gets the same one).</summary>
    public static int HostRate => Volatile.Read(ref hostRate);

    public static void SetHostRate(int rate)
    {
        if (rate <= 0 || rate == HostRate) return;
        Volatile.Write(ref hostRate, rate);
        lock (Gate) foreach (var e in slots) e?.HostRateChanged();
    }

    public static int Count { get { lock (Gate) return slots.Count(s => s != null); } }

    public static Emulator? Get(int id) { lock (Gate) return id is >= 1 and <= MaxEmulators ? slots[id - 1] : null; }

    public static List<Emulator> All() { lock (Gate) return slots.Where(s => s != null).Select(s => s!).ToList(); }

    private static Emulator Create(int id, bool auto)
    {
        var e = new Emulator(id, auto);
        slots[id - 1] = e;
        return e;
    }

    /// <summary>(Re)places an instance: releases what it held, then applies its Emulator and Channel parameters. <paramref name="hintEmulator"/> /
    /// <paramref name="hintChannel"/> (where it sat before, or was saved) are preferred when its parameters say Auto.</summary>
    internal static void Resolve(Bn2Plugin who, int hintEmulator = 0, int hintChannel = -1)
    {
        lock (Gate)
        {
            ResolveLocked(who, hintEmulator, hintChannel);
            RetryWaiting(who);
        }
    }

    internal static void Leave(Bn2Plugin who)
    {
        lock (Gate)
        {
            var old = who.Assignment;
            who.SetAssignment(Assignment.None);
            orphans.Remove(who);
            Drop(old.Emulator, who);
            RetryWaiting(who);
        }
    }

    /// <summary>Takes an instance out of an emulator; an emulator with no instance left is closed (its game and chips freed).</summary>
    private static void Drop(Emulator? e, Bn2Plugin who)
    {
        if (e == null) return;
        e.RemoveMember(who);
        if (e.MemberCount == 0 && slots[e.Id - 1] == e)
        {
            slots[e.Id - 1] = null;
            e.Dispose();
        }
    }

    internal static void ResolveLocked(Bn2Plugin who, int hintEmulator, int hintChannel)
    {
        var old = who.Assignment;
        old.Emulator?.ReleaseClaim(who);

        int reqEmulator = who.Get(P.Emulator), reqChannel = who.Get(P.Channel);
        Assignment result;
        if (reqEmulator > 0)
        {
            var e = slots[reqEmulator - 1] ?? Create(reqEmulator, auto: false);
            result = e.Attach(who, reqChannel, hintChannel, explicitEmulator: true)!;
        }
        else
        {
            result = TryAuto(who, reqChannel, hintEmulator, hintChannel) ?? NoRoom(who);
        }

        who.SetAssignment(result);
        if (result.Emulator == null) { if (!orphans.Contains(who)) orphans.Add(who); }
        else orphans.Remove(who);
        if (old.Emulator != null && old.Emulator != result.Emulator) Drop(old.Emulator, who);
    }

    /// <summary>A channel or an emulator may just have become free: instances that were left without one (a channel taken by another
    /// instance, no room anywhere) get another try, one pass, oldest emulator first.</summary>
    private static void RetryWaiting(Bn2Plugin except)
    {
        if (retrying) return;
        retrying = true;
        try
        {
            var waiting = new List<Bn2Plugin>();
            foreach (var e in slots) if (e != null) foreach (var m in e.MembersSnapshot()) if (m != except && m.Assignment.Problem != null) waiting.Add(m);
            waiting.AddRange(orphans.Where(m => m != except));
            foreach (var m in waiting)
            {
                var a = m.Assignment;
                ResolveLocked(m, hintEmulator: m.Get(P.Emulator) == 0 ? a.Emulator?.Id ?? 0 : 0, hintChannel: -1);
            }
        }
        finally { retrying = false; }
    }

    private static Assignment? TryAuto(Bn2Plugin who, int reqChannel, int hintEmulator, int hintChannel)
    {
        // 1. where it was (or was saved): keep the numbering of a project stable
        if (hintEmulator is >= 1 and <= MaxEmulators)
        {
            var e = slots[hintEmulator - 1];
            if (e == null) e = Create(hintEmulator, auto: true);
            if (e.AutoCreated)
            {
                var a = e.Attach(who, reqChannel, hintChannel, explicitEmulator: false);
                if (a != null) return a;
                if (e.MemberCount == 0) { slots[e.Id - 1] = null; e.Dispose(); }
            }
        }
        // 2. the first auto emulator with room: the fewest emulators
        foreach (var e in slots)
        {
            if (e == null || !e.AutoCreated) continue;
            var a = e.Attach(who, reqChannel, hintChannel, explicitEmulator: false);
            if (a != null) return a;
        }
        // 3. a new one, at the lowest free number
        for (int i = 0; i < MaxEmulators; i++)
            if (slots[i] == null)
            {
                var e = Create(i + 1, auto: true);
                return e.Attach(who, reqChannel, hintChannel, explicitEmulator: false);
            }
        return null;
    }

    private static Assignment NoRoom(Bn2Plugin who) => new(null, -1, $"all {MaxEmulators} emulators are in use");

    /// <summary>The emulator's mode changed: Auto channels are chosen again (Auto in ROM mode means the whole game's mix first) and
    /// Mix, which exists only in ROM mode, is given up or taken as the mode dictates.</summary>
    internal static void ReResolveAll(Emulator e)
    {
        foreach (var m in e.MembersSnapshot())
            ResolveLocked(m, hintEmulator: m.Get(P.Emulator) == 0 ? e.Id : 0, hintChannel: -1);
    }
}
