using System;
using NesEmulator.Mix;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace NesEmulator.Mix;

/// <summary>
/// A Sega console session whose picture can be drawn by a NES picture chip (and, through the NES, a Game Boy or SNES one): it wraps the real session (the Master System, Game Gear or Genesis board keeps running
/// exactly as it would - the Z80 or 68000, the VDP's registers and memory, the sound) and, when the PPU menu names a NES chip (<c>NES:FIXS</c>, <c>NES:DMG</c>, ...), turns the VDP's state into that chip's picture after each frame
/// (<see cref="SmsToNes"/>, <see cref="MdToNes"/>). With the console's own chip selected it is a plain pass-through. Swapping the picture chip is in place: the board never notices, so the game keeps running.
/// </summary>
/// <remarks>Counters of everything the translation could not carry are in <see cref="Counters"/> and in <see cref="Description"/> (the status line and the HTTP API show it); <see cref="ReferenceCanvas"/> is the Sega
/// chip's own picture on the same canvas, for the fidelity measurement.</remarks>
public sealed class SegaPictureSession : IConsoleSession, IPictureBridgeInfo
{
    private readonly IConsoleSession inner;
    private readonly BOARD_SMS? sms;
    private readonly BOARD_MD? md;
    private SmsLineCapture? smsCap;
    private SmsToNes? toNesSms;
    private MdToNes? toNesMd;
    private string ppuId;
    private readonly uint[] frame = new uint[256 * 240], reference = new uint[256 * 240];
    private (int X, int Y, int W, int H) region = (0, 0, 256, 240);
    private bool referenceSame = true;

    public SegaPictureSession(IConsoleSession inner, string ppu)
    {
        this.inner = inner;
        sms = (inner as SmsSession)?.Board;
        md = (inner as MdSession)?.Board;
        if (sms == null && md == null) throw new ArgumentException("SegaPictureSession wraps a Master System, Game Gear or Genesis session");
        ppuId = ppu;
        if (IsNesChip(ppu)) SetBridge(ppu);
    }

    /// <summary>The wrapped board session (tools).</summary>
    public IConsoleSession Inner => inner;

    private static bool IsNesChip(string id) => id.StartsWith("NES:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of the picture chip in use: the console's own, or <c>NES:&lt;chip&gt;</c>.</summary>
    public string PictureChip => ppuId;
    public bool Bridged => toNesSms != null || toNesMd != null;

    private void SetBridge(string id)
    {
        toNesSms = null; toNesMd = null;
        string chip = id[4..];
        if (sms != null)
        {
            smsCap ??= new SmsLineCapture(sms);
            toNesSms = new SmsToNes(chip, inner.Console == ConsoleKind.GameGear);
        }
        else toNesMd = new MdToNes(md!, chip);
        referenceSame = sms != null;
    }

    private void ClearBridge() { toNesSms = null; toNesMd = null; }

    // ---- save states: the board's (the picture chip keeps no state of its own; the bridge is rebuilt from the loaded VDP) ----
    public bool SupportsState => inner.SupportsState;
    public string StateUnsupportedReason => inner.StateUnsupportedReason;
    public byte[] SaveState() => inner.SaveState();

    public void LoadState(byte[] state)
    {
        inner.LoadState(state);
        if (IsNesChip(ppuId)) SetBridge(ppuId);   // the translators' caches and the line capture describe the old picture
    }

    // ---------------------------------------------------------------------------------------------------------- IConsoleSession
    public ConsoleKind Console => inner.Console;
    public string Title => inner.Title;
    public string Description => Bridged ? $"{inner.Description} | PICTURE {ppuId} [{Counters.Summary(5)}]" : inner.Description;
    public double FramesPerSecond => inner.FramesPerSecond;
    public void SetPad(int player, PadButtons buttons) => inner.SetPad(player, buttons);
    public void Reset() => inner.Reset();
    public uint[] Frame => Bridged ? frame : inner.Frame;
    public int FrameWidth => Bridged ? 256 : inner.FrameWidth;
    public int FrameHeight => Bridged ? 240 : inner.FrameHeight;
    public int DisplayWidth => Bridged ? 256 : inner.DisplayWidth;
    public int SampleRate => inner.SampleRate;
    public int ReadSamples(short[] buffer) => inner.ReadSamples(buffer);
    public bool HasBattery => inner.HasBattery;
    public byte[] ExportSave() => inner.ExportSave();
    public void ImportSave(byte[] data) => inner.ImportSave(data);
    public string GameId => inner.GameId;
    public void Dispose() => inner.Dispose();

    public void RunFrame()
    {
        inner.RunFrame();
        if (toNesSms != null)
        {
            var rgba = toNesSms.Render(sms!.Vdp, smsCap!);
            ConsoleSessions.RgbaToArgb(rgba, frame);
            BuildSmsReference(toNesSms.Window);
        }
        else if (toNesMd != null)
        {
            var rgba = toNesMd.Render();
            ConsoleSessions.RgbaToArgb(rgba, frame);
            toNesMd.BuildReference(reference, out var r);
            region = Viewed(r);
        }
    }

    public string SwapRefusal(CoreSlot slot, string id) => !ConsoleSessions.Offers(Console, slot, id) ? ConsoleSessions.NotOffered(Console, slot, id)
        : slot != CoreSlot.Ppu ? inner.SwapRefusal(slot, id)
        : IsNesChip(id) ? $"the picture bridge to {id} could not be built for this {Consoles.DisplayName(Console)} game"
        : $"the {Consoles.DisplayName(Console)} has one picture chip of its own ({CoreCatalog.Default(Console, CoreSlot.Ppu)}) and {id} is not it";

    public bool TrySwapCore(CoreSlot slot, string id)
    {
        if (!ConsoleSessions.Offers(Console, slot, id)) return false;     // an id the menu does not list is refused, whichever slot
        if (slot != CoreSlot.Ppu) return inner.TrySwapCore(slot, id);
        if (IsNesChip(id))
        {
            try { SetBridge(id); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException) { ClearBridge(); return false; }   // the app restarts the game on it and says why
            ppuId = id; return true;
        }
        // the console's own chip is the only other picture chip: any other id is not a choice this console has (the app restarts the game on it, which then says so)
        if (!id.Equals(CoreCatalog.Default(Console, CoreSlot.Ppu), StringComparison.OrdinalIgnoreCase) || !inner.TrySwapCore(slot, id)) return false;
        ClearBridge(); ppuId = id; return true;
    }

    // ---------------------------------------------------------------------------------------------------------- IPictureBridgeInfo
    public string BridgeName => toNesSms != null ? $"{Consoles.DisplayName(Console)} on NES:{toNesSms.NesPpuId}" : toNesMd != null ? $"Genesis on NES:{toNesMd.NesPpuId}" : "none";
    public BridgeCounters Counters => toNesSms?.Counters ?? toNesMd?.Counters ?? emptyCounters;
    private static readonly BridgeCounters emptyCounters = new();
    public uint[] ReferenceCanvas => reference;
    public uint[] BridgeCanvas => frame;
    public int CanvasWidth => 256;
    public int CanvasHeight => 240;
    public (int X, int Y, int W, int H) Region => region;
    public bool ReferenceIsSameFrame => referenceSame;

    private void BuildSmsReference(PictureWindow w)
    {
        Array.Clear(reference);
        var src = sms!.Vdp.Frame;
        for (int y = 0; y < w.H; y++)
            for (int x = 0; x < w.W; x++) reference[(w.OffsetY + y) * 256 + w.OffsetX + x] = 0xFF000000u | src[(w.Y0 + y) * 256 + w.X0 + x];
        region = Viewed((w.OffsetX, w.OffsetY, w.W, w.H));
    }

    /// <summary>The part of a region the chosen chip shows at all: the plain Game Boy chip (DMG) shows 160 x 144 of the 256 x 240, every other NES-family chip the whole picture.</summary>
    internal (int X, int Y, int W, int H) Viewed((int X, int Y, int W, int H) r)
    {
        if (!ppuId.Equals("NES:DMG", StringComparison.OrdinalIgnoreCase)) return r;
        int x0 = Math.Max(r.X, MixConfig.GbCropX), y0 = Math.Max(r.Y, MixConfig.GbCropY), x1 = Math.Min(r.X + r.W, MixConfig.GbCropX + 160), y1 = Math.Min(r.Y + r.H, MixConfig.GbCropY + 144);
        return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}
