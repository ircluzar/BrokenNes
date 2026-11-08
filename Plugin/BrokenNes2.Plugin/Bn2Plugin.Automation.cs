// Dynamic automation (prototype, 2026-11-07): any knob of the plugin can be automated in FL, not only the 14 fixed parameters.
//
// FL learns the number of parameters once, when the plugin is created, so the plugin declares a pool of blank parameters after the 14
// (`AutoSlots`, indexes P.Count and up). A right-click on a knob that is not one of the 14 (the prototype: an Instruments tab field)
// wires the knob to a free slot (`BindAutomation`), renames the slot after the knob, tells FL its names changed (FHD_NamesChanged) and
// opens FL's own parameter menu for the slot ("Create automation clip", "Link to controller" ...). A slot's value is 0..1000, mapped onto
// the knob's own range. FL may move a slot from the audio thread: the value is only stored there, and applied on FL's GUI thread
// (`PumpAutomation`, from Idle and from the editor's timer), where the Instruments model may be called. The wiring is kept by the
// project (section AUT1), by the knob's identity (instrument + field id), not by a position in a list.
using System.Text;
using BrokenNes.Fruity;
using BrokenNes2.Ui;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    /// <summary>How many blank parameters follow the fixed ones (append-only: a saved project's clips point at these numbers).</summary>
    public const int AutoSlots = 64;
    public const int AutoBase = P.Count;
    public const int AutoRange = 1000;
    private const uint AutoTag = 0x31545541;              // "AUT1"
    /// <summary>FHD_NamesChanged (fp_plugclass.h): "the names of the parameters have changed", Index = the FPN_ constant of what changed.</summary>
    private const int FhdNamesChanged = 3;

    /// <summary>What a slot is wired to: an Instruments tab field of the frozen game.</summary>
    private sealed record AutoBinding(int Instrument, string FieldId, string Name, int Min, int Max);

    private readonly AutoBinding?[] autoBindings = new AutoBinding?[AutoSlots];
    private readonly int[] autoPending = new int[AutoSlots];
    private readonly int[] autoApplied = new int[AutoSlots];
    private volatile bool autoAnyPending;
    private int autoAppliedCount;

    public static FruityParam[] BuildParams()
    {
        var fixedOnes = Bn2Params.Build();
        var all = new FruityParam[P.Count + AutoSlots];
        fixedOnes.CopyTo(all, 0);
        for (int i = 0; i < AutoSlots; i++) all[P.Count + i] = new FruityParam($"Free slot {i + 1}", 0, AutoRange, 0, 0, v => v.ToString());
        return all;
    }

    public override string GetName(int section, int index, int value)
    {
        if (index >= AutoBase && index < AutoBase + AutoSlots)
        {
            var b = autoBindings[index - AutoBase];
            if (section == Fpn.Param) return b == null ? $"Free slot {index - AutoBase + 1}" : b.Name;
            if (section == Fpn.ParamValue) return b == null ? value.ToString() : FieldValue(b, value).ToString();
        }
        return base.GetName(section, index, value);
    }

    private static int FieldValue(AutoBinding b, int slotValue) =>
        b.Min + (int)Math.Round((double)Math.Clamp(slotValue, 0, AutoRange) * (b.Max - b.Min) / AutoRange);

    private static int SlotValue(AutoBinding b, int fieldValue) =>
        b.Max == b.Min ? 0 : (int)Math.Round((double)(Math.Clamp(fieldValue, b.Min, b.Max) - b.Min) * AutoRange / (b.Max - b.Min));

    /// <summary>Called from <see cref="OnParamChanged"/> for a slot (any thread, FL's automation included): store only.</summary>
    private void AutomationMoved(int slot, int value)
    {
        Volatile.Write(ref autoPending[slot], value + 1);   // +1: 0 = nothing pending
        autoAnyPending = true;
    }

    /// <summary>Wires a knob to a slot (the GUI thread): the slot it already has, else the first free one; -1 when the pool is full.</summary>
    internal int BindAutomation(int instrument, string fieldId, string name, int min, int max, int current)
    {
        int free = -1;
        for (int i = 0; i < AutoSlots; i++)
        {
            var b = autoBindings[i];
            if (b != null && b.Instrument == instrument && b.FieldId == fieldId) return AutoBase + i;
            if (b == null && free < 0) free = i;
        }
        if (free < 0) return -1;
        var nb = new AutoBinding(instrument, fieldId, name, min, max);
        autoBindings[free] = nb;
        autoApplied[free] = current;
        Volatile.Write(ref Values[AutoBase + free], SlotValue(nb, current));
        Host.Dispatcher(HostTag, FhdNamesChanged, Fpn.Param, 0);
        if (HostLog.On) HostLog.Text(InstanceNo, $"automation: slot {free + 1} wired to {name} ({fieldId} of instrument {instrument})");
        return AutoBase + free;
    }

    /// <summary>A wired knob was moved in the editor: FL hears it as the slot's change (automation recording, "last tweaked").</summary>
    internal void AutomatedKnobEdited(int instrument, string fieldId, int value)
    {
        for (int i = 0; i < AutoSlots; i++)
            if (autoBindings[i] is { } b && b.Instrument == instrument && b.FieldId == fieldId)
            {
                autoApplied[i] = value;
                int v = SlotValue(b, value);
                Volatile.Write(ref Values[AutoBase + i], v);
                Host.OnParamChanged(HostTag, AutoBase + i, v);
            }
    }

    /// <summary>Applies the slots FL moved since the last call (FL's GUI thread only).</summary>
    internal void PumpAutomation()
    {
        if (!autoAnyPending) return;
        autoAnyPending = false;
        UiBoot.Run();                                       // the models register when an editor opens: a project reopened with its window closed must still be automated
        var emu = Emulator;
        var factory = UiServices.Instruments;
        IInstrumentModelProvider? provider = null;
        for (int i = 0; i < AutoSlots; i++)
        {
            int p = Interlocked.Exchange(ref autoPending[i], 0);
            if (p == 0 || autoBindings[i] is not { } b) continue;
            int fv = FieldValue(b, p - 1);
            if (fv == autoApplied[i]) continue;
            if (emu is not { Runaway: true } || factory == null) { autoAnyPending = true; Volatile.Write(ref autoPending[i], p); continue; }   // kept until there is a frozen game
            provider ??= factory(emu);
            if (provider == null) return;
            var r = provider.SetField(b.Instrument, b.FieldId, fv);
            if (r.Ok) { autoApplied[i] = fv; autoAppliedCount++; }
        }
    }

    internal int AutomationAppliedCount => autoAppliedCount;

    /// <summary>Test hooks 960-969: 960 wire field number <paramref name="index"/> of instrument <paramref name="value"/> (as the right-click does; returns the slot's parameter
    /// index, -1 none), 961 pump (as FL's Idle), 962 the field's value now (field number index of instrument value), 963 applied count, 964 slot index's name length.</summary>
    private nint AutomationTest(int hook, nint index, nint value)
    {
        UiBoot.Run();
        var emu = Emulator;
        var prov = emu is { Runaway: true } && UiServices.Instruments is { } fac ? fac(emu) : null;
        switch (hook)
        {
            case 960:
            {
                if (prov == null) return emu == null ? -2 : !emu.Runaway ? -4 : UiServices.Instruments == null ? -5 : -6;
                var fs = prov.Fields((int)value);
                if ((uint)index >= (uint)fs.Count) return -3;
                var f = fs[(int)index];
                return BindAutomation((int)value, f.Id, $"{prov.Instruments[(int)value].Name}: {f.Name}", f.Min, f.Max, f.Value);
            }
            case 961: PumpAutomation(); return 0;
            case 962:
            {
                if (prov == null) return int.MinValue;
                var fs = prov.Fields((int)value);
                return (uint)index < (uint)fs.Count ? fs[(int)index].Value : int.MinValue;
            }
            case 963: return autoAppliedCount;
            case 964: return AutomationName((int)index)?.Length ?? -1;
        }
        return 0;
    }
    internal string? AutomationName(int slot) => (uint)slot < AutoSlots ? autoBindings[slot]?.Name : null;

    private byte[]? AutomationSectionBytes()
    {
        int n = 0;
        foreach (var b in autoBindings) if (b != null) n++;
        if (n == 0) return null;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(1);            // version
        w.Write(n);
        for (int i = 0; i < AutoSlots; i++)
            if (autoBindings[i] is { } b) { w.Write(i); w.Write(b.Instrument); w.Write(b.FieldId); w.Write(b.Name); w.Write(b.Min); w.Write(b.Max); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Reads AUT1: checked, never trusted (a damaged one wires nothing).</summary>
    private void RestoreAutomationSection(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            if (r.ReadInt32() != 1) return;
            int n = r.ReadInt32();
            if (n < 0 || n > AutoSlots) return;
            var read = new AutoBinding?[AutoSlots];
            for (int k = 0; k < n; k++)
            {
                int slot = r.ReadInt32(), inst = r.ReadInt32();
                string id = r.ReadString(), name = r.ReadString();
                int min = r.ReadInt32(), max = r.ReadInt32();
                if ((uint)slot >= AutoSlots || inst < 0 || id.Length is 0 or > 200 || name.Length > 200 || max < min) return;
                read[slot] = new AutoBinding(inst, id, name, min, max);
            }
            for (int i = 0; i < AutoSlots; i++) { autoBindings[i] = read[i]; autoApplied[i] = int.MinValue; }
            Host.Dispatcher(HostTag, FhdNamesChanged, Fpn.Param, 0);
        }
        catch (EndOfStreamException) { }
    }
}
