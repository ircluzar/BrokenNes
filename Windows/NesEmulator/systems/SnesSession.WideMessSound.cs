using System.IO;
using NesEmulator.Snes;

namespace NesEmulator.Systems;

/// <summary>The WideMess sound chip's part of a WideMess state (spec section 4; format wms-2): after the WideMess registers and the picture chip, a flag, then the sound chip's own part (<see cref="APU_WMS.SaveWide"/>)
/// when the unit that plays, or the one parked behind the silent stand-in, is the WideMess's. A SNES sound chip on the WideMess console has no such part.</summary>
public sealed partial class SnesSession
{
    /// <summary>The WideMess sound chip of this session: the unit playing, or the one parked behind HLE; null when the sound is a SNES chip.</summary>
    public APU_WMS? WideMessSound => (Spc ?? parkedSpc) as APU_WMS;

    private void WriteWideSound(BinaryWriter w)
    {
        var s = WideMessSound;
        w.Write(s != null);
        s?.SaveWide(w);
    }

    private void ReadWideSound(BinaryReader r)
    {
        bool has = r.ReadBoolean();
        var s = WideMessSound;
        if (has)
        {
            if (s == null) throw new InvalidDataException($"The state carries the WideMess sound chip's part, but the sound unit here ({apuId}) is not the WideMess's.");
            s.LoadWide(r);
        }
        else s?.PowerOnWide();   // saved with a SNES sound chip: the WideMess part is what it is at power-on
        if (board.WideMess != null) board.WideMess.Sound = Spc as APU_WMS;
    }
}
