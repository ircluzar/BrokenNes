using System;
using System.Collections.Generic;
using NesEmulator.Corruption;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Corruption;

/// <summary>
/// A console game that cannot keep its state, for the tests: a real session behind a wrapper that says <see cref="SupportsState"/> is false (and, like any session without states, throws if it is asked to save or load one).
/// Every console has save states now, so this is how the replay-from-power-on path of the corruption library (the anchor a console without states needs) stays tested: the machine is real, only its states are taken away.
/// </summary>
internal sealed class StatelessSession : IConsoleSession, IMemoryDomainSource
{
    public readonly IConsoleSession Inner;
    public StatelessSession(IConsoleSession inner) { Inner = inner; }

    /// <summary>The real session under any number of wrappers.</summary>
    public static IConsoleSession Real(IConsoleSession s) { while (s is StatelessSession w) s = w.Inner; return s; }

    public IReadOnlyList<IMemoryDomain> MemoryDomains => MachineDomains.For(Inner);

    public ConsoleKind Console => Inner.Console;
    public string Title => Inner.Title;
    public string Description => Inner.Description;
    public double FramesPerSecond => Inner.FramesPerSecond;
    public void SetPad(int player, PadButtons buttons) => Inner.SetPad(player, buttons);
    public void RunFrame() => Inner.RunFrame();
    public void Reset() => Inner.Reset();
    public uint[] Frame => Inner.Frame;
    public int FrameWidth => Inner.FrameWidth;
    public int FrameHeight => Inner.FrameHeight;
    public int DisplayWidth => Inner.DisplayWidth;
    public int SampleRate => Inner.SampleRate;
    public int ReadSamples(short[] buffer) => Inner.ReadSamples(buffer);
    public bool HasBattery => Inner.HasBattery;
    public byte[] ExportSave() => Inner.ExportSave();
    public void ImportSave(byte[] data) => Inner.ImportSave(data);
    public string GameId => Inner.GameId;
    public bool TrySwapCore(CoreSlot slot, string id) => Inner.TrySwapCore(slot, id);
    public string SwapRefusal(CoreSlot slot, string id) => Inner.SwapRefusal(slot, id);
    public void Dispose() => Inner.Dispose();

    public bool SupportsState => false;
    public string StateUnsupportedReason => "this test console has no save states";
}
