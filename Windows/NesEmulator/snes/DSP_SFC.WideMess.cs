using System;
using System.IO;

namespace NesEmulator.Snes;

/// <summary>
/// The WideMess sound chip's voices 8-31 (docs/projects/widemess-spec.md section 4), as more voices of this same S-DSP: three banks of 8 in a second register page ($80-$FF
/// through $F2 / $F3), samples in BRR or 16-bit PCM anywhere in 1 MB of sound RAM (20-bit addresses; the first 64 KB is the SPC700's), mixed into the S-DSP's main and
/// echo sums after voice 7 so the master volume, the echo and FLG apply to all 32. Only <see cref="APU_WMS"/> attaches them (<see cref="AttachWide"/>), and they run only
/// while the console is unlocked (<see cref="WideOn"/>): a SNES S-DSP, or a locked WideMess, pays one bool test a sample and is the S-DSP it always was.
/// The per-voice arithmetic is the S-DSP's own (<see cref="Interpolate"/>, <see cref="RunEnvelope"/>, the rate counter, the noise); the BRR decoder is repeated here with
/// 20-bit addresses rather than touching the SNES voices' decoder.
/// </summary>
public sealed partial class DSP_SFC
{
    public const int WideVoiceCount = 24, WideBanks = 3, SoundRamBytes = 0x100000;
    /// <summary>The value of WID (second page $1D) once unlocked.</summary>
    public const byte WideId = 0x57;

    // second-page offsets that are the same register in every bank
    private const int WBANK = 0x0C, WID = 0x1D, WDIRL = 0x5D, WDIRH = 0x6D;

    private byte[]? wideRam;            // sound RAM $10000-$FFFFF (APU_WMS's); null: a SNES S-DSP
    private Voice[]? wideVoices;
    private byte[]? wideRegs;           // the three banks' second pages, 128 bytes each
    private bool[]? widePcm;            // per voice: playing a 16-bit PCM sample (else BRR)
    private byte[]? wideNewKon, wideKon, wideKoff;
    private bool wideOn;
    private byte wbank, wdirL, wdirH;

    /// <summary>True while voices 8-31 and the second page are live (the console is unlocked).</summary>
    public bool WideOn => wideOn;

    /// <summary>Gives this S-DSP the WideMess voices (<see cref="APU_WMS"/>'s constructor): <paramref name="high"/> is the sound RAM above the first 64 KB (960 KB). They stay off until <see cref="SetWideOn"/>.</summary>
    public void AttachWide(byte[] high)
    {
        if (high.Length != SoundRamBytes - 0x10000) throw new ArgumentException($"the sound RAM above 64 KB is {SoundRamBytes - 0x10000:N0} bytes", nameof(high));
        wideRam = high;
        wideVoices = new Voice[WideVoiceCount];
        for (int i = 0; i < WideVoiceCount; i++) wideVoices[i] = new Voice();
        wideRegs = new byte[WideBanks * 128];
        widePcm = new bool[WideVoiceCount];
        wideNewKon = new byte[WideBanks]; wideKon = new byte[WideBanks]; wideKoff = new byte[WideBanks];
        ResetWide();
    }

    /// <summary>The power-on state of voices 8-31 and the second page (all off; the console locked).</summary>
    public void ResetWide()
    {
        wideOn = false; wbank = 0; wdirL = 0; wdirH = 0;
        if (wideVoices == null) return;
        foreach (var v in wideVoices)
        {
            Array.Clear(v.Buf);
            v.BufPos = v.InterpPos = v.BrrAddr = v.KonDelay = v.Env = v.HiddenEnv = 0;
            v.BrrOffset = 1; v.Mode = EnvMode.Release;
        }
        Array.Clear(wideRegs!); Array.Clear(widePcm!); Array.Clear(wideNewKon!); Array.Clear(wideKon!); Array.Clear(wideKoff!);
    }

    /// <summary>The unlock line (spec 4): on, the second page and voices 8-31 are live. Throws on a SNES S-DSP (nothing attached), never pretends.</summary>
    public void SetWideOn(bool on)
    {
        if (on && wideRam == null) throw new InvalidOperationException("this S-DSP has no WideMess voices (only APU_WMS attaches them)");
        wideOn = on;
    }

    /// <summary>A $F3 read with $F2 = $80-$FF: the second page once unlocked; on a SNES (or locked) the read-only mirror of $00-$7F.</summary>
    public byte ReadHigh(int addr)
    {
        if (!wideOn) return Regs[addr & 0x7F];
        int e = addr & 0x7F;
        switch (e)
        {
            case WBANK: return wbank;
            case WID: return WideId;
            case WDIRL: return wdirL;
            case WDIRH: return wdirH;
        }
        int b = wbank & 3;
        if (b == 3) return 0;
        int low = e & 0x0F;
        if (low is 0x0A or 0x0B or 0x0E)
        {
            int a = wideVoices![b * 8 + (e >> 4)].BrrAddr;
            return (byte)(low == 0x0A ? a : low == 0x0B ? a >> 8 : a >> 16);
        }
        return wideRegs![b * 128 + e];
    }

    /// <summary>A $F3 write with $F2 = $80-$FF: the second page once unlocked; on a SNES (or locked) it goes nowhere.</summary>
    public void WriteHigh(int addr, byte value)
    {
        if (!wideOn) return;
        int e = addr & 0x7F;
        switch (e)
        {
            case WBANK: wbank = value; return;
            case WID: return;
            case WDIRL: wdirL = value; return;
            case WDIRH: wdirH = (byte)(value & 0x0F); return;
        }
        int b = wbank & 3;
        if (b == 3) return;
        int low = e & 0x0F;
        if (low is 0x0A or 0x0B or 0x0E) return;                  // the playing address is read-only
        wideRegs![b * 128 + e] = value;
        if (e == KON) wideNewKon![b] = value;
        else if (e == ENDX) wideRegs[b * 128 + ENDX] = 0;         // any write clears the bank's end flags
    }

    /// <summary>A byte of the 1 MB sound RAM (20-bit address).</summary>
    private int SoundByte(int a)
    {
        a &= SoundRamBytes - 1;
        return a < 0x10000 ? ram[a] : wideRam![a - 0x10000];
    }

    private int Address20(int a) => SoundByte(a) | SoundByte(a + 1) << 8 | (SoundByte(a + 2) & 0x0F) << 16;

    /// <summary>The byte address of a sample's 16-byte directory entry.</summary>
    private int WideEntry(int srcn) => (wdirH << 16 | wdirL << 8) + srcn * 16;

    // =====================================================================================
    //  One sample of voices 8-31 (called by RunSample after voice 7, before the echo)
    // =====================================================================================

    private void RunWide(ref int mainL, ref int mainR, ref int echoL, ref int echoR, int prevOutput)
    {
        var regs = wideRegs!; var vs = wideVoices!; var pcmOf = widePcm!;
        var newKonB = wideNewKon!; var konB = wideKon!; var koffB = wideKoff!;
        if (everyOther)
            for (int b = 0; b < WideBanks; b++) { newKonB[b] &= (byte)~konB[b]; konB[b] = newKonB[b]; koffB[b] = regs[b * 128 + KOFF]; }
        bool softReset = (Regs[FLG] & 0x80) != 0;

        for (int b = 0; b < WideBanks; b++)
        {
            int rb = b * 128;
            byte pmon = regs[rb + PMON], non = regs[rb + NON], eon = regs[rb + EON];
            byte kon = konB[b], koff = koffB[b];
            byte endx = regs[rb + ENDX];
            for (int i = 0; i < 8; i++)
            {
                int n = b * 8 + i;
                var v = vs[n];
                int bit = 1 << i, r = rb + (i << 4);
                bool pcm = pcmOf[n];

                int pitch = regs[r + 2] | (regs[r + 3] & 0x3F) << 8;
                int header = 0, brrByte = 0;
                if (!pcm) { header = SoundByte(v.BrrAddr); brrByte = SoundByte(v.BrrAddr + v.BrrOffset); }

                if ((pmon & bit) != 0) pitch += ((prevOutput >> 5) * pitch) >> 10;

                if (v.KonDelay != 0)
                {
                    if (v.KonDelay == 5)
                    {
                        // key-on: the start address and the format from the directory
                        int entry = WideEntry(regs[r + 4]);
                        v.BrrAddr = Address20(entry);
                        pcm = pcmOf[n] = (SoundByte(entry + 3) & 1) != 0;
                        v.BrrOffset = 1; v.BufPos = 0;
                        header = 0;   // ignored on this sample, as on the S-DSP
                    }
                    v.Env = 0; v.HiddenEnv = 0;
                    v.InterpPos = (--v.KonDelay & 3) != 0 ? 0x4000 : 0;
                    pitch = 0;
                }

                int output;
                if (v.Env == 0) output = 0;
                else
                {
                    output = (non & bit) != 0 ? (short)(noise * 2) : Interpolate(v);
                    output = ((output * v.Env) >> 11) & ~1;
                }
                regs[r + 8] = (byte)(v.Env >> 4);   // ENVX

                if (softReset || (!pcm && (header & 3) == 1)) { v.Mode = EnvMode.Release; v.Env = 0; }

                if (everyOther)
                {
                    if ((koff & bit) != 0) v.Mode = EnvMode.Release;
                    if ((kon & bit) != 0) { v.KonDelay = 5; v.Mode = EnvMode.Attack; }
                }
                if (v.KonDelay == 0) RunEnvelope(v, regs[r + 5], regs[r + 6], regs[r + 7]);

                bool looped = false;
                if (v.InterpPos >= 0x4000)
                {
                    if (pcm) looped = DecodePcm(v, regs[r + 4]);
                    else
                    {
                        DecodeBrrWide(v, header, brrByte);
                        if ((v.BrrOffset += 2) >= 9)
                        {
                            v.BrrAddr = (v.BrrAddr + 9) & (SoundRamBytes - 1);
                            if ((header & 1) != 0) { v.BrrAddr = Address20(WideEntry(regs[r + 4]) + 4); looped = true; }
                            v.BrrOffset = 1;
                        }
                    }
                }
                v.InterpPos = Math.Min((v.InterpPos & 0x3FFF) + pitch, 0x7FFF);

                int ampL = (output * (sbyte)regs[r]) >> 7, ampR = (output * (sbyte)regs[r + 1]) >> 7;
                mainL = Clamp16(mainL + ampL); mainR = Clamp16(mainR + ampR);
                if ((eon & bit) != 0) { echoL = Clamp16(echoL + ampL); echoR = Clamp16(echoR + ampR); }

                if (looped) endx |= (byte)bit;
                if (v.KonDelay == 5) endx &= (byte)~bit;
                regs[r + 9] = (byte)(output >> 8);   // OUTX
                prevOutput = output;
            }
            regs[rb + ENDX] = endx;
        }
    }

    /// <summary>The next 4 samples of a 16-bit PCM voice; at the sample's end, the loop address (and silence when the sample does not loop). True when it reached the end.</summary>
    private bool DecodePcm(Voice v, int srcn)
    {
        int a = v.BrrAddr, pos = v.BufPos;
        v.BufPos = pos + 4 >= 12 ? 0 : pos + 4;
        for (int k = 0; k < 4; k++, pos++, a += 2)
        {
            int s = (short)(SoundByte(a) | SoundByte(a + 1) << 8);
            v.Buf[pos] = v.Buf[pos + 12] = s;
        }
        int entry = WideEntry(srcn);
        if (a >= Address20(entry + 8))
        {
            v.BrrAddr = Address20(entry + 4);
            if ((SoundByte(entry + 3) & 2) == 0) { v.Mode = EnvMode.Release; v.Env = 0; }
            return true;
        }
        v.BrrAddr = a & (SoundRamBytes - 1);
        return false;
    }

    /// <summary><see cref="DecodeBrr"/> with 20-bit addresses (the same arithmetic, kept apart so the SNES voices' decoder is untouched).</summary>
    private void DecodeBrrWide(Voice v, int header, int firstByte)
    {
        int nybbles = firstByte << 8 | SoundByte(v.BrrAddr + v.BrrOffset + 1);
        int shift = header >> 4, filter = header & 0x0C;
        int pos = v.BufPos;
        v.BufPos = v.BufPos + 4 >= 12 ? 0 : v.BufPos + 4;

        for (int i = 0; i < 4; i++, pos++, nybbles <<= 4)
        {
            int s = (short)nybbles >> 12;
            s = (s << shift) >> 1;
            if (shift >= 0x0D) s = (s >> 25) << 11;

            int p1 = v.Buf[pos + 11], p2 = v.Buf[pos + 10] >> 1;
            if (filter >= 8)
            {
                s += p1 - p2;
                if (filter == 8) { s += p2 >> 4; s += (p1 * -3) >> 6; }
                else { s += (p1 * -13) >> 7; s += (p2 * 3) >> 4; }
            }
            else if (filter != 0) { s += p1 >> 1; s += (-p1) >> 5; }

            s = (short)(Clamp16(s) * 2);
            v.Buf[pos] = v.Buf[pos + 12] = s;
        }
    }

    // =====================================================================================
    //  State (the WideMess part; the S-DSP's own is SaveState / LoadState)
    // =====================================================================================

    private const byte WideStateVersion = 1;

    public void SaveWide(BinaryWriter w)
    {
        if (wideVoices == null) throw new InvalidOperationException("this S-DSP has no WideMess voices to save");
        SnesStateIo.Begin(w, WideStateVersion);
        w.Write(wideOn); w.Write(wbank); w.Write(wdirL); w.Write(wdirH);
        SnesStateIo.Put(w, wideRegs!);
        SnesStateIo.Put(w, widePcm!);
        SnesStateIo.Put(w, wideNewKon!); SnesStateIo.Put(w, wideKon!); SnesStateIo.Put(w, wideKoff!);
        foreach (var v in wideVoices)
        {
            SnesStateIo.Put(w, v.Buf);
            w.Write(v.BufPos); w.Write(v.InterpPos); w.Write(v.BrrAddr); w.Write(v.BrrOffset); w.Write(v.KonDelay); w.Write(v.Env); w.Write(v.HiddenEnv);
            w.Write((int)v.Mode);
        }
    }

    public void LoadWide(BinaryReader r)
    {
        if (wideVoices == null) throw new InvalidOperationException("this S-DSP has no WideMess voices to load");
        SnesStateIo.Expect(r, WideStateVersion, "WideMess voices");
        wideOn = r.ReadBoolean(); wbank = r.ReadByte(); wdirL = r.ReadByte(); wdirH = (byte)(r.ReadByte() & 0x0F);
        SnesStateIo.Get(r, wideRegs!);
        SnesStateIo.Get(r, widePcm!);
        SnesStateIo.Get(r, wideNewKon!); SnesStateIo.Get(r, wideKon!); SnesStateIo.Get(r, wideKoff!);
        foreach (var v in wideVoices)
        {
            SnesStateIo.Get(r, v.Buf);
            v.BufPos = r.ReadInt32(); v.InterpPos = r.ReadInt32(); v.BrrAddr = r.ReadInt32() & (SoundRamBytes - 1); v.BrrOffset = r.ReadInt32(); v.KonDelay = r.ReadInt32(); v.Env = r.ReadInt32(); v.HiddenEnv = r.ReadInt32();
            if (v.BufPos is < 0 or > 8 || v.BrrOffset is < 1 or > 9 || v.KonDelay is < 0 or > 5) throw new InvalidDataException("The WideMess voices part of the state holds a voice position out of range.");
            int mode = r.ReadInt32();
            if (mode is < 0 or > 3) throw new InvalidDataException($"The WideMess voices part of the state holds an envelope mode of {mode}.");
            v.Mode = (EnvMode)mode;
        }
    }
}
