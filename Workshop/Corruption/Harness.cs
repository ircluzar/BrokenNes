using System;
using System.Collections.Generic;
using System.Linq;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>The counting and printing every check uses.</summary>
internal sealed class Checks
{
    public int Passed, Failed, NotRun;
    private readonly List<string> failures = new();

    public void Ok(string name, bool ok, string detail = "")
    {
        if (ok) Passed++; else { Failed++; failures.Add(name); }
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}" + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }

    /// <summary>A control: the check's own negative. It passes when the sabotaged version is caught (the check is able to fail).</summary>
    public void Control(string name, bool caught, string detail = "")
    {
        if (caught) Passed++; else { Failed++; failures.Add("control: " + name); }
        Console.WriteLine($"  {(caught ? "PASS" : "FAIL")}  control: {name}" + (detail.Length > 0 ? $"   [{detail}]" : ""));
    }

    public void Skip(string name, string why)
    {
        NotRun++;
        Console.WriteLine($"  NOT RUN  {name}   [{why}]");
    }

    public int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"{Passed} passed, {Failed} failed, {NotRun} not run" + (Failed > 0 ? ": " + string.Join("; ", failures) : ""));
        return Failed == 0 ? 0 : 1;
    }
}

internal static class Hash
{
    public static ulong Fnv(ReadOnlySpan<byte> d)
    {
        ulong h = 1469598103934665603UL;
        foreach (byte b in d) h = (h ^ b) * 1099511628211UL;
        return h;
    }
    public static ulong Fnv(string s) => Fnv(System.Text.Encoding.UTF8.GetBytes(s));
    public static ulong Fnv(ReadOnlySpan<uint> d)
    {
        ulong h = 1469598103934665603UL;
        foreach (uint u in d) h = (h ^ u) * 1099511628211UL;
        return h;
    }
    public static ulong Fnv(ReadOnlySpan<short> d)
    {
        ulong h = 1469598103934665603UL;
        foreach (short s in d) h = (h ^ (ushort)s) * 1099511628211UL;
        return h;
    }
}

/// <summary>A session under test: runs frames, folds the audio it makes into a hash, and hashes the picture.</summary>
internal class SessionRun : IDisposable
{
    public readonly IConsoleSession Session;
    private readonly short[] buf = new short[32768];
    private ulong audio = 1469598103934665603UL;
    public long AudioSamples;
    public int Peak;
    public int Frames;

    public SessionRun(IConsoleSession session) { Session = session; }

    public ulong AudioHash => audio;

    /// <summary>Starts the audio hash (and the sample count) over: two runs that are compared from a common point must not carry the audio of how they got there.</summary>
    public void ResetAudio() { audio = 1469598103934665603UL; AudioSamples = 0; Peak = 0; }

    public void Run(int frames, Func<int, PadButtons>? pad = null)
    {
        for (int i = 0; i < frames; i++)
        {
            Session.SetPad(0, pad?.Invoke(Frames) ?? PadButtons.None);
            Session.RunFrame();
            int n;
            while ((n = Session.ReadSamples(buf)) > 0)
            {
                AudioSamples += n;
                for (int k = 0; k < n; k++) { audio = (audio ^ (ushort)buf[k]) * 1099511628211UL; Peak = Math.Max(Peak, Math.Abs((int)buf[k])); }
            }
            Frames++;
        }
    }

    public ulong Picture() => Hash.Fnv(Session.Frame.AsSpan(0, Session.FrameWidth * Session.FrameHeight));

    public virtual void Dispose() => Session.Dispose();
}

/// <summary>A SNES session under test, with its memories.</summary>
internal sealed class SnesRun : SessionRun
{
    public SnesSession Snes => (SnesSession)Session;

    public SnesRun(byte[] rom, string apu = "SFC")
        : base(ConsoleSessions.Create(ConsoleKind.Snes, (byte[])rom.Clone(), "SFC", "SFC", apu, null, "test.sfc")) { }

    public SnesRun(IConsoleSession existing) : base(existing) { }

    public NesEmulator.Snes.APU_SFC Spc => Snes.Spc ?? throw new InvalidOperationException("no SPC700");
    public ulong Aram() => Hash.Fnv(Spc.Aram);
    public ulong Dsp() => Hash.Fnv(Spc.Dsp.Regs);
    public ulong Wram() => Hash.Fnv(Snes.Board.Wram);
}
