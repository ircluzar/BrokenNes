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
    private readonly uint[] frame = new uint[ExtPicture.MaxViewWidth * ExtPicture.MaxLines], reference = new uint[ExtPicture.MaxViewWidth * ExtPicture.MaxLines];
    private (int X, int Y, int W, int H) region = (0, 0, 256, 240);
    /// <summary>The size of the last bridged picture: the console's own on an enhanced chip (the Genesis's 320 or 256 x 224 or 240, the Master System's 256 x 192 / 224 / 240, the Game Gear's 160 x 144), else the NES's 256 x 240.</summary>
    private int frameWidth = 256, frameHeight = 240;
    private bool guestSize;
    /// <summary>The columns the WideMess widescreen chip shows on each side of the console's own picture (0 on every other chip): the picture is 400 wide, the console's own in the middle.</summary>
    private int side;
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

    /// <summary>A picture chip that is not the console's own: a NES chip (<c>NES:&lt;chip&gt;</c>) or another console's enhanced chip reached directly (<c>SNES:SFCS</c>, <c>MD:MDS</c>, ...).</summary>
    private static bool IsNesChip(string id) => id.StartsWith("NES:", StringComparison.OrdinalIgnoreCase) || CoreCatalog.IsDirectRoute(id);

    /// <summary>The id of the picture chip in use: the console's own, or <c>NES:&lt;chip&gt;</c>.</summary>
    public string PictureChip => ppuId;
    public bool Bridged => toNesSms != null || toNesMd != null;

    private void SetBridge(string id)
    {
        toNesSms = null; toNesMd = null;
        bool gg = inner.Console == ConsoleKind.GameGear;
        if (CoreCatalog.IsDirectRoute(id))
        {
            // another console's enhanced picture chip, reached directly: the console's own picture as layers at its own size, in that chip's colours
            var chip = EnhancedChips.Picture(id);
            if (sms != null) { smsCap ??= new SmsLineCapture(sms); toNesSms = new SmsToNes(chip, id, gg); }
            else toNesMd = new MdToNes(md!, chip, id);
        }
        else
        {
            string chip = id[4..];
            if (sms != null)
            {
                smsCap ??= new SmsLineCapture(sms);
                toNesSms = new SmsToNes(chip, gg);
            }
            else toNesMd = new MdToNes(md!, chip);
        }
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
    /// <summary>True while an enhanced chip draws the console's own picture size (the frame is the console's picture at the top left, not a NES picture with it placed on the 256 x 240).</summary>
    public bool GuestSize => Bridged && guestSize;
    /// <summary>The enhanced chip drawing the picture, null on the console's own chip or a stock NES chip (tests read its layers).</summary>
    internal IEnhancedPictureChip? Enhanced => toNesSms?.Enhanced ?? toNesMd?.Enhanced;
    public int FrameWidth => Bridged ? frameWidth : inner.FrameWidth;
    public int FrameHeight => Bridged ? frameHeight : inner.FrameHeight;
    /// <summary>On an enhanced chip the console's own proportions (the Genesis's 320 for both its widths, as its own chip shows them); a NES chip's picture is 256 wide.</summary>
    public int DisplayWidth => !Bridged ? inner.DisplayWidth : side > 0 ? frameWidth : guestSize && md != null ? 320 : frameWidth;
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
            Take(toNesSms.Render(sms!.Vdp, smsCap!));
            BuildSmsReference(toNesSms.Window);
        }
        else if (toNesMd != null)
        {
            Take(toNesMd.Render());
            toNesMd.BuildReference(reference, frameWidth, out var r);
            region = Viewed(r);
        }
        if (side > 0) ShiftReference();
        if (Enhanced is WmsWideChip wide) wide.CountInto(Counters);     // what the widescreen chip showed beside the console's picture (the status line)
    }

    private void Take(BridgeFrame f)
    {
        frameWidth = f.Width; frameHeight = f.Height; guestSize = f.GuestSize; side = f.Side;
        Array.Copy(f.Argb, frame, f.Width * f.Height);
    }

    /// <summary>On the widescreen chip the console's own picture is the middle of the frame: the reference (laid out from the left) moves right by the side's columns, and so does its region.</summary>
    private void ShiftReference()
    {
        for (int y = 0; y < frameHeight; y++)
        {
            int row = y * frameWidth;
            for (int x = frameWidth - 1; x >= side; x--) reference[row + x] = reference[row + x - side];
            for (int x = 0; x < side; x++) reference[row + x] = 0;
        }
        region = (region.X + side, region.Y, region.W, region.H);
    }

    public string SwapRefusal(CoreSlot slot, string id) => !ConsoleSessions.Offers(Console, slot, id) ? ConsoleSessions.NotOffered(Console, slot, id)
        : slot != CoreSlot.Ppu ? inner.SwapRefusal(slot, id)
        : IsNesChip(id) ? $"the picture bridge to {id} could not be built for this {Consoles.DisplayName(Console)} game"
        : $"the {Consoles.DisplayName(Console)}'s own picture chips are {string.Join(" and ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(CoreCatalog.Options(Console, CoreSlot.Ppu), o => o.Section == CoreSection.Own), o => o.Id))}, and {id} is not one of them";

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
        // the console's own chips are the only other picture chips: stock (the default) and enhanced (a subclass of it, drawing the console's own game on the stock pipeline); any other id is not a choice this console
        // has (the app restarts the game on it, which then says so)
        if (!IsOwnChip(id) || !inner.TrySwapCore(slot, id)) return false;
        ClearBridge(); ppuId = id; return true;
    }

    /// <summary>The console's own picture chips: its stock chip and, once it exists, its enhanced one (its own game draws on the stock pipeline on both).</summary>
    private bool IsOwnChip(string id) => System.Linq.Enumerable.Any(CoreCatalog.Options(Console, CoreSlot.Ppu), o => o.Section == CoreSection.Own && o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------------------------------------------------- IPictureBridgeInfo
    public string BridgeName => toNesSms != null ? $"{Consoles.DisplayName(Console)} on {Route(toNesSms.NesPpuId)}" : toNesMd != null ? $"Genesis on {Route(toNesMd.NesPpuId)}" : "none";
    private static string Route(string id) => id.Contains(':') ? id : "NES:" + id;
    public BridgeCounters Counters => toNesSms?.Counters ?? toNesMd?.Counters ?? emptyCounters;
    private static readonly BridgeCounters emptyCounters = new();
    public uint[] ReferenceCanvas => reference;
    public uint[] BridgeCanvas => frame;
    public int CanvasWidth => frameWidth;
    public int CanvasHeight => frameHeight;
    public (int X, int Y, int W, int H) Region => region;
    public bool ReferenceIsSameFrame => referenceSame;

    private void BuildSmsReference(PictureWindow w)
    {
        Array.Clear(reference);
        var src = sms!.Vdp.Frame;
        for (int y = 0; y < w.H; y++)
            for (int x = 0; x < w.W; x++) reference[(w.OffsetY + y) * frameWidth + w.OffsetX + x] = 0xFF000000u | src[(w.Y0 + y) * 256 + w.X0 + x];
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
