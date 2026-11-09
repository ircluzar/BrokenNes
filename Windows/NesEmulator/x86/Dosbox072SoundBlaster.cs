// DOSBox 0.72's Sound Blaster (the DSP, its DMA paths, ADPCM, the CT1345 mixer), ported line by line to C# for BrokenNes as an "art instrument".
//
// Source:   DOSBox 0.72, SVN tags/RELEASE_0_72, src/hardware/sblaster.cpp ($Id: sblaster.cpp,v 1.66 2007-08-08 qbix79)
//           https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_72/src/hardware/sblaster.cpp
// Authors:  The DOSBox Team (Copyright (C) 2002-2007)
// Licence:  GPL-2.0-or-later - documented exception, see THIRD_PARTY_NOTICES.md section 2a.
//
// The card as DOSBox 0.72 builds it here: sbtype sbpro2 (DSP version 3.02), base 220h, IRQ 7, DMA 1 (DOSBox 0.72's dosbox.conf defaults: irq=7, dma=1,
// hdma=5; its own default type sb16 is not used: the user's pick is the SB Pro 2), mixer=true, the SB channel installed at 22050 Hz and re-clocked to the
// DSP rate, the 0.72 mixer at its default 22050 Hz (DosboxMixer, PcCards.cs), held onto the console's 44.1 kHz. DOS programs find it through
// SET BLASTER=A220 I7 D1 T4. Its FM: the board's AdLib (Opl2, ymfm) answers 388h/389h and the card's 220h-223h / 228h-229h; DOSBox 0.72 put an OPL3
// there for an SB Pro 2 (oplmode auto) - the OPL here is an OPL2 (deliberate: the card's FM is the console's AdLib).
// Not ported: MIDI (0x38 is dropped: no MIDI device), CMS / Game Blaster, SB16-only commands answer as on an SB Pro (DSP_SB16_ONLY), the mixer's
// volumes act on the DSP channel only (FM volume regs are stored, the AdLib's own level is kept).
//
// Faithful port: the flaws are KEPT, not fixed. Characteristic quirks of 0.72:
//  - Direct DAC (10h) is not timed: every write in a 1 ms mixer tick is collected (each stored twice) and stretched over the tick (AddStretched),
//    so DAC playback is smeared to the 1 ms grid and its pitch follows how many writes land per tick.
//  - The speaker starts OFF (D1h needed); with it off, DMA still runs silently on PIC events (DMA_Silent_Event) in min-size steps.
//  - DMA is pulled by the mixer in 1 ms blocks, not at the DSP rate; the IRQ comes when the block that empties the count is mixed (up to ~1 ms late),
//    short tails (< 3 ms) are scheduled as END_DMA_Event.
//  - 40h (time constant) during auto-init DMA re-prepares the transfer ("nasty kind of hack"): the count restarts.
//  - D0h (halt) only pauses (MODE_DMA_PAUSE) - "games sometimes already program a new dma before stopping"; D4h resumes only from that pause.
//  - Reset (226h) never leaves high-speed mode (TODO in the source), does not touch the speaker, the read buffer is flushed only on the 0 write.
//  - Write-status (22Ch) reads busy on every 8th of 16 reads (write_busy counter), reads FFh while in reset.
//  - 22Ah repeats its last value when the buffer is empty (static, "JANGLE DEMO").
//  - 0x80 (silence) is timed by sb.freq, not the time constant actually given; ADC (24h) is faked: DMA writes 128s at once and raises the IRQ.
//  - ADPCM reference bytes reset the step to 0; bad codes are clamped; ADPCM-3 low code is (b & 3) << 1 (2.6-bit).
//  - The mixer channel's volume: master/31 * 10^((31-vol)*-1.3/20) ("a bit less" than the manual's 2 dB steps).
//  - IRQ 2/5/7/10 select and DMA 0/1/3 select through mixer 80h/81h are taken (a program can move the card); 82h reads the pending flags.

using System;
using System.IO;

namespace NesEmulator.X86;

public sealed class Dosbox072SoundBlaster : IPcSoundCard, IPcSoundOutput
{
    public string Id => "SB_D072";

    private const int DMA_BUFSIZE = 1024, DSP_BUFSIZE = 64, DSP_DACSIZE = 512, SB_SH = 14, SB_SH_MASK = (1 << SB_SH) - 1;
    private const int DSP_S_RESET = 0, DSP_S_NORMAL = 1;
    private const int SBT_1 = 1, SBT_PRO1 = 2, SBT_2 = 3, SBT_PRO2 = 4, SBT_16 = 6;
    private const int MODE_NONE = 0, MODE_DAC = 1, MODE_DMA = 2, MODE_DMA_PAUSE = 3, MODE_DMA_MASKED = 4;
    private const int DSP_DMA_NONE = 0, DSP_DMA_2 = 1, DSP_DMA_3 = 2, DSP_DMA_4 = 3, DSP_DMA_8 = 4, DSP_DMA_16 = 5, DSP_DMA_16_ALIASED = 6;
    private const int EV_END_DMA = 1, EV_SILENT = 2, EV_RAISE = 3;

    private readonly PcMachine _m;
    private readonly DosboxMixer _mix;
    private readonly DosboxMixer.Chan _chan;
    public long IrqsRaised { get; private set; }

    // ---- struct SB_INFO ----
    private int freq;
    private bool dma_stereo, dma_sign, dma_autoinit;
    private int dma_mode, dma_rate, dma_mul, dma_total, dma_left, dma_min, dma_remain_size;
    private readonly byte[] dma_b8 = new byte[DMA_BUFSIZE * 2];
    private int dma_chan = -1;     // the DMA channel in use (DOSBox: sb.dma.chan)
    private bool speaker, midi;
    private byte time_constant;
    private int mode;
    private readonly int type = SBT_PRO2;
    private bool irq_pending_8bit, irq_pending_16bit;
    private byte dsp_state, dsp_cmd, dsp_cmd_len, dsp_cmd_in_pos;
    private readonly byte[] dsp_in = new byte[DSP_BUFSIZE];
    private int dsp_in_pos;
    private readonly byte[] dsp_out = new byte[DSP_BUFSIZE];
    private int dsp_out_pos, dsp_out_used;
    private byte dsp_test_register;
    private int dsp_write_busy;
    private readonly short[] dac_data = new short[DSP_DACSIZE + 1];
    private int dac_used;
    private short dac_last;
    private byte mixer_index;
    private readonly byte[] mixer_dac = new byte[2], mixer_fm = new byte[2], mixer_cda = new byte[2], mixer_master = new byte[2], mixer_lin = new byte[2];
    private byte mixer_mic;
    private bool mixer_stereo, mixer_enabled = true, mixer_filtered;
    private readonly byte[] mixer_unhandled = new byte[0x48];
    private byte adpcm_reference;
    private int adpcm_stepsize;
    private bool adpcm_haveref;
    private int hw_base = 0x220, hw_irq = 7, hw_dma8 = 1, hw_dma16 = 5;
    private int e2_value, e2_count;
    private byte read_data_last;   // DSP_ReadData's static
    private int cb_kind;           // which DMA callback is registered: 0 none, 1 DSP_DMA_CallBack, 2 E2, 3 ADC

    private static readonly byte[] DSP_cmd_len =
    {
        0,0,0,0, 0,2,0,0, 0,0,0,0, 0,0,2,1,
        1,0,0,0, 2,0,2,2, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 2,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0, 1,0,0,0, 0,0,0,0,
        1,2,2,0, 0,0,0,0, 2,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 2,2,2,2, 0,0,0,0, 0,0,0,0,
        2,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        3,3,3,3, 3,3,3,3, 3,3,3,3, 3,3,3,3,
        3,3,3,3, 3,3,3,3, 3,3,3,3, 3,3,3,3,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        1,0,1,0, 1,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
    };

    private static readonly int[,] E2_incr_table =
    {
        {  0x01, -0x02, -0x04,  0x08, -0x10,  0x20,  0x40, -0x80, -106 },
        { -0x01,  0x02, -0x04,  0x08,  0x10, -0x20,  0x40, -0x80,  165 },
        { -0x01,  0x02,  0x04, -0x08,  0x10, -0x20, -0x40,  0x80, -151 },
        {  0x01, -0x02,  0x04, -0x08, -0x10,  0x20, -0x40,  0x80,   90 },
    };

    private const string copyright_string = "COPYRIGHT (C) CREATIVE TECHNOLOGY LTD, 1992.";

    public Dosbox072SoundBlaster(PcMachine m)
    {
        _m = m;
        _mix = new DosboxMixer(() => m.PitNow, 22050) { };
        _mix.OnEvent = OnEvent;
        _chan = _mix.AddChannel(SBLASTER_CallBack, 22050);
        Reset();
    }

    public void Reset()
    {
        // the SBLASTER constructor's part after the channel install
        ClearCallback();
        hw_base = 0x220; hw_irq = 7; hw_dma8 = 1; hw_dma16 = 5;
        mixer_enabled = true; mixer_stereo = false;
        dsp_state = DSP_S_NORMAL;
        dsp_cmd = 0; dsp_out_used = dsp_out_pos = 0; read_data_last = 0; mode = MODE_NONE;
        _mix.Restart(true);
        _chan.Enabled = false;
        DSP_Reset();
        CTMIXER_Reset();
        speaker = false;
        _chan.Enable(false);
        midi = false;
    }

    private void OnEvent(int code, int val)
    {
        switch (code)
        {
            case EV_END_DMA: END_DMA_Event(val); break;
            case EV_SILENT: DMA_Silent_Event(val); break;
            case EV_RAISE: SB_RaiseIRQ(false); break;
        }
    }

    // ---- the DMA callback plumbing (DmaChannel::Register_Callback) ----
    private Dma8237.Channel? Chan(int n) => n is >= 0 and < 8 ? _m.Dma[n] : null;
    private void ClearCallback() { foreach (var c in _m.Dma.Channels) if (c.Callback == DmaCb) c.Callback = null; cb_kind = 0; }
    private void Register(int ch, int kind)
    {
        var c = Chan(ch); if (c == null) return;
        foreach (var o in _m.Dma.Channels) if (o != c && o.Callback == DmaCb) o.Callback = null;
        cb_kind = kind;
        c.RegisterCallback(kind == 0 ? null : DmaCb);
    }
    private void DmaCb(Dma8237.Channel c, DmaEvent e)
    {
        switch (cb_kind)
        {
            case 1: DSP_DMA_CallBack(c, e); break;
            case 2: DSP_E2_DMA_CallBack(c, e); break;
            case 3: DSP_ADC_CallBack(c, e); break;
        }
    }

    private void DSP_SetSpeaker(bool how)
    {
        if (speaker == how) return;
        speaker = how;
        if (type == SBT_16) return;
        _chan.Enable(how);
        if (speaker) { _mix.RemoveEvents(EV_SILENT); CheckDMAEnd(); }
    }

    private void SB_RaiseIRQ(bool sixteen)
    {
        IrqsRaised++;
        if (hw_irq is >= 0 and < 16) _m.Pic.Pulse(hw_irq);
        if (sixteen) irq_pending_16bit = true; else irq_pending_8bit = true;
    }

    private void DSP_FlushData() { dsp_out_used = 0; dsp_out_pos = 0; }

    private void DSP_DMA_CallBack(Dma8237.Channel chan, DmaEvent ev)
    {
        if (ev == DmaEvent.ReachedTc) return;
        else if (ev == DmaEvent.Masked)
        {
            if (mode == MODE_DMA) { GenerateDMASound(dma_min); mode = MODE_DMA_MASKED; }
        }
        else if (ev == DmaEvent.Unmasked)
        {
            if (mode == MODE_DMA_MASKED && dma_mode != DSP_DMA_NONE) { DSP_ChangeMode(MODE_DMA); CheckDMAEnd(); }
        }
    }

    private const int MIN_ADAPTIVE_STEP_SIZE = 0;

    private static readonly sbyte[] scaleMap4 =
    {
        0,  1,  2,  3,  4,  5,  6,  7,  0,  -1,  -2,  -3,  -4,  -5,  -6,  -7,
        1,  3,  5,  7,  9, 11, 13, 15, -1,  -3,  -5,  -7,  -9, -11, -13, -15,
        2,  6, 10, 14, 18, 22, 26, 30, -2,  -6, -10, -14, -18, -22, -26, -30,
        4, 12, 20, 28, 36, 44, 52, 60, -4, -12, -20, -28, -36, -44, -52, -60,
    };
    private static readonly byte[] adjustMap4 =
    {
          0, 0, 0, 0, 0, 16, 16, 16,   0, 0, 0, 0, 0, 16, 16, 16,
        240, 0, 0, 0, 0, 16, 16, 16, 240, 0, 0, 0, 0, 16, 16, 16,
        240, 0, 0, 0, 0, 16, 16, 16, 240, 0, 0, 0, 0, 16, 16, 16,
        240, 0, 0, 0, 0,  0,  0,  0, 240, 0, 0, 0, 0,  0,  0,  0,
    };
    private static readonly sbyte[] scaleMap2 = { 0, 1, 0, -1, 1, 3, -1, -3, 2, 6, -2, -6, 4, 12, -4, -12, 8, 24, -8, -24, 6, 48, -16, -48 };
    private static readonly byte[] adjustMap2 = { 0, 4, 0, 4, 252, 4, 252, 4, 252, 4, 252, 4, 252, 4, 252, 4, 252, 4, 252, 4, 252, 0, 252, 0 };
    private static readonly sbyte[] scaleMap3 =
    {
        0, 1, 2, 3, 0, -1, -2, -3, 1, 3, 5, 7, -1, -3, -5, -7, 2, 6, 10, 14, -2, -6, -10, -14,
        4, 12, 20, 28, -4, -12, -20, -28, 5, 15, 25, 35, -5, -15, -25, -35,
    };
    private static readonly byte[] adjustMap3 =
    {
          0, 0, 0, 8,   0, 0, 0, 8, 248, 0, 0, 8, 248, 0, 0, 8, 248, 0, 0, 8, 248, 0, 0, 8,
        248, 0, 0, 8, 248, 0, 0, 8, 248, 0, 0, 0, 248, 0, 0, 0,
    };

    private static byte DecodeAdpcm(int sample, ref byte reference, ref int scale, sbyte[] scaleMap, byte[] adjustMap)
    {
        int samp = sample + scale;
        if (samp < 0) samp = 0;
        if (samp > scaleMap.Length - 1) samp = scaleMap.Length - 1;
        int r = reference + scaleMap[samp];
        reference = r > 0xff ? (byte)0xff : r < 0 ? (byte)0 : (byte)r;
        scale = (scale + adjustMap[samp]) & 0xff;
        return reference;
    }

    private readonly byte[] _mixTemp = new byte[DMA_BUFSIZE * 4 + 8];

    private void GenerateDMASound(int size)
    {
        int read = 0, done = 0, i = 0;
        if (dma_autoinit) { if (dma_left <= size) size = dma_left; }
        else if (dma_left <= dma_min) size = dma_left;
        size = Math.Min(size, DMA_BUFSIZE);
        var ch = Chan(dma_chan);
        switch (dma_mode)
        {
            case DSP_DMA_2:
            case DSP_DMA_3:
            case DSP_DMA_4:
                read = ch?.Read(size, dma_b8, 0) ?? 0;
                if (read > 0 && adpcm_haveref) { adpcm_haveref = false; adpcm_reference = dma_b8[0]; adpcm_stepsize = MIN_ADAPTIVE_STEP_SIZE; i++; }
                for (; i < read; i++)
                {
                    byte b = dma_b8[i];
                    if (dma_mode == DSP_DMA_2)
                        for (int sh = 6; sh >= 0; sh -= 2) _mixTemp[done++] = DecodeAdpcm((b >> sh) & 3, ref adpcm_reference, ref adpcm_stepsize, scaleMap2, adjustMap2);
                    else if (dma_mode == DSP_DMA_3)
                    {
                        _mixTemp[done++] = DecodeAdpcm((b >> 5) & 7, ref adpcm_reference, ref adpcm_stepsize, scaleMap3, adjustMap3);
                        _mixTemp[done++] = DecodeAdpcm((b >> 2) & 7, ref adpcm_reference, ref adpcm_stepsize, scaleMap3, adjustMap3);
                        _mixTemp[done++] = DecodeAdpcm((b & 3) << 1, ref adpcm_reference, ref adpcm_stepsize, scaleMap3, adjustMap3);
                    }
                    else
                    {
                        _mixTemp[done++] = DecodeAdpcm(b >> 4, ref adpcm_reference, ref adpcm_stepsize, scaleMap4, adjustMap4);
                        _mixTemp[done++] = DecodeAdpcm(b & 0xf, ref adpcm_reference, ref adpcm_stepsize, scaleMap4, adjustMap4);
                    }
                }
                _chan.AddMono(done, k => (sbyte)(_mixTemp[k] ^ 0x80) << 8);
                break;
            case DSP_DMA_8:
                if (dma_stereo)
                {
                    read = ch?.Read(size, dma_b8, dma_remain_size) ?? 0;
                    int total = read + dma_remain_size;
                    if (!dma_sign) _chan.AddSamples(total >> 1, k => (sbyte)(dma_b8[k * 2] ^ 0x80) << 8, k => (sbyte)(dma_b8[k * 2 + 1] ^ 0x80) << 8);
                    else _chan.AddSamples(total >> 1, k => (sbyte)dma_b8[k * 2] << 8, k => (sbyte)dma_b8[k * 2 + 1] << 8);
                    if ((total & 1) != 0) { dma_remain_size = 1; dma_b8[0] = dma_b8[total - 1]; } else dma_remain_size = 0;
                }
                else
                {
                    read = ch?.Read(size, dma_b8, 0) ?? 0;
                    if (!dma_sign) _chan.AddMono(read, k => (sbyte)(dma_b8[k] ^ 0x80) << 8);
                    else _chan.AddMono(read, k => (sbyte)dma_b8[k] << 8);
                }
                break;
            case DSP_DMA_16:
            case DSP_DMA_16_ALIASED:
            {
                // (SB16 only: never reached on the SB Pro 2, kept for the aliased path's shape)
                read = (ch?.Read(size, dma_b8, dma_remain_size * 2) ?? 0) >> (dma_mode == DSP_DMA_16_ALIASED ? 1 : 0);
                int total = read + dma_remain_size;
                Func<int, int> s16 = k => dma_sign ? (short)(dma_b8[k * 2] | (dma_b8[k * 2 + 1] << 8)) : (short)((dma_b8[k * 2] | (dma_b8[k * 2 + 1] << 8)) ^ 0x8000);
                if (dma_stereo)
                {
                    _chan.AddSamples(total >> 1, k => s16(k * 2), k => s16(k * 2 + 1));
                    if ((total & 1) != 0) { dma_remain_size = 1; dma_b8[0] = dma_b8[(total - 1) * 2]; dma_b8[1] = dma_b8[(total - 1) * 2 + 1]; } else dma_remain_size = 0;
                }
                else _chan.AddMono(read, s16);
                if (dma_mode == DSP_DMA_16_ALIASED) read <<= 1;
                break;
            }
            default:
                mode = MODE_NONE;
                return;
        }
        dma_left -= read;
        if (dma_left == 0)
        {
            _mix.RemoveEvents(EV_END_DMA);
            if (!dma_autoinit) { mode = MODE_NONE; dma_mode = DSP_DMA_NONE; }
            else { dma_left = dma_total; if (dma_left == 0) mode = MODE_NONE; }
            SB_RaiseIRQ(dma_mode >= DSP_DMA_16);
        }
    }

    private void DMA_Silent_Event(int val)
    {
        if (dma_left < val) val = dma_left;
        int read = Chan(dma_chan)?.Read(val, dma_b8, 0) ?? 0;
        dma_left -= read;
        if (dma_left == 0)
        {
            SB_RaiseIRQ(dma_mode >= DSP_DMA_16);
            if (dma_autoinit) dma_left = dma_total;
            else { mode = MODE_NONE; dma_mode = DSP_DMA_NONE; }
        }
        if (dma_left != 0)
        {
            int bigger = dma_left > dma_min ? dma_min : dma_left;
            float delay = bigger * 1000.0f / Math.Max(1, dma_rate);
            _mix.AddEvent(EV_SILENT, delay, bigger);
        }
    }

    private void END_DMA_Event(int val) => GenerateDMASound(val);

    private void CheckDMAEnd()
    {
        if (dma_left == 0) return;
        if (!speaker && type != SBT_16)
        {
            int bigger = dma_left > dma_min ? dma_min : dma_left;
            float delay = bigger * 1000.0f / Math.Max(1, dma_rate);
            _mix.AddEvent(EV_SILENT, delay, bigger);
        }
        else if (dma_left < dma_min)
        {
            float delay = dma_left * 1000.0f / Math.Max(1, dma_rate);
            _mix.AddEvent(EV_END_DMA, delay, dma_left);
        }
    }

    private void DSP_ChangeMode(int m)
    {
        if (mode == m) return;
        _chan.FillUp();
        mode = m;
    }

    private void DSP_DoDMATransfer(int m, int f, bool stereo)
    {
        mode = MODE_DMA_MASKED;
        _chan.FillUp();
        dma_left = dma_total;
        dma_mode = m;
        dma_stereo = stereo;
        irq_pending_8bit = false;
        irq_pending_16bit = false;
        switch (m)
        {
            case DSP_DMA_2: dma_mul = (1 << SB_SH) / 4; break;
            case DSP_DMA_3: dma_mul = (1 << SB_SH) / 3; break;
            case DSP_DMA_4: dma_mul = (1 << SB_SH) / 2; break;
            case DSP_DMA_8: dma_mul = 1 << SB_SH; break;
            case DSP_DMA_16_ALIASED: dma_mul = (1 << SB_SH) * 2; break;
            case DSP_DMA_16: dma_mul = 1 << SB_SH; break;
            default: return;
        }
        if (dma_stereo) dma_mul *= 2;
        dma_rate = (freq * dma_mul) >> SB_SH;
        dma_min = dma_rate * 3 / 1000;
        _chan.SetFreq(Math.Max(1, f));
        dma_mode = m;
        _mix.RemoveEvents(EV_END_DMA);
        Register(dma_chan, 1);
    }

    private void DSP_PrepareDMA_Old(int m, bool autoinit, bool sign)
    {
        dma_autoinit = autoinit;
        dma_sign = sign;
        if (!autoinit) dma_total = 1 + dsp_in[0] + (dsp_in[1] << 8);
        dma_chan = hw_dma8;
        DSP_DoDMATransfer(m, freq / (mixer_stereo ? 2 : 1), mixer_stereo);
    }

    private void DSP_AddData(byte val)
    {
        if (dsp_out_used < DSP_BUFSIZE)
        {
            int start = dsp_out_used + dsp_out_pos;
            if (start >= DSP_BUFSIZE) start -= DSP_BUFSIZE;
            dsp_out[start] = val;
            dsp_out_used++;
        }
    }

    private void DSP_Reset()
    {
        DSP_ChangeMode(MODE_NONE);
        dsp_cmd_len = 0;
        dsp_in_pos = 0;
        dsp_write_busy = 0;
        dma_left = 0; dma_total = 0;
        dma_stereo = false; dma_sign = false; dma_autoinit = false;
        dma_mode = DSP_DMA_NONE;
        dma_remain_size = 0;
        freq = 22050;
        time_constant = 45;
        dac_used = 0; dac_last = 0;
        e2_value = 0xaa; e2_count = 0;
        irq_pending_8bit = false; irq_pending_16bit = false;
        _chan.SetFreq(22050);
        _mix.RemoveEvents(EV_END_DMA);
    }

    private void DSP_DoReset(byte val)
    {
        if ((val & 1) != 0) { DSP_Reset(); dsp_state = DSP_S_RESET; }
        else { DSP_FlushData(); DSP_AddData(0xaa); dsp_state = DSP_S_NORMAL; }
    }

    private void DSP_E2_DMA_CallBack(Dma8237.Channel chan, DmaEvent ev)
    {
        if (ev == DmaEvent.Unmasked)
        {
            var c = _m.Dma[hw_dma8];
            cb_kind = 0; c.Callback = null;   // chan->Register_Callback(0)
            c.Write(1, new[] { (byte)e2_value }, 0);
        }
    }

    private void DSP_ADC_CallBack(Dma8237.Channel chan, DmaEvent ev)
    {
        if (ev != DmaEvent.Unmasked) return;
        var c = _m.Dma[hw_dma8];
        var one = new byte[] { 128 };
        while (dma_left-- != 0) c.Write(1, one, 0);
        dma_left = 0;
        SB_RaiseIRQ(false);
        cb_kind = 0; c.Callback = null;
    }

    private void DSP_DoCommand()
    {
        bool sb2above = type > SBT_1, sb16 = type == SBT_16;
        switch (dsp_cmd)
        {
            case 0x04: DSP_FlushData(); DSP_AddData(0xff); break;
            case 0x10:
                DSP_ChangeMode(MODE_DAC);
                if (dac_used < DSP_DACSIZE)
                {
                    dac_data[dac_used++] = (short)((sbyte)(dsp_in[0] ^ 0x80) << 8);
                    dac_data[dac_used++] = (short)((sbyte)(dsp_in[0] ^ 0x80) << 8);
                }
                break;
            case 0x24:
                dma_left = dma_total = 1 + dsp_in[0] + (dsp_in[1] << 8);
                dma_sign = false;
                Register(hw_dma8, 3);
                break;
            case 0x14:
            case 0x91:
                DSP_PrepareDMA_Old(DSP_DMA_8, false, false);
                break;
            case 0x90:
            case 0x1c:
                if (!sb2above) break;
                DSP_PrepareDMA_Old(DSP_DMA_8, true, false);
                break;
            case 0x38: break;   // MIDI out: no MIDI device
            case 0x40:
                freq = 1000000 / (256 - dsp_in[0]);
                if (dma_mode != DSP_DMA_NONE && dma_autoinit) DSP_PrepareDMA_Old(dma_mode, dma_autoinit, dma_sign);
                break;
            case 0x41:
            case 0x42:
                if (!sb16) break;
                freq = (dsp_in[0] << 8) | dsp_in[1];
                break;
            case 0x48:
                if (!sb2above) break;
                dma_total = 1 + dsp_in[0] + (dsp_in[1] << 8);
                break;
            case 0x75: adpcm_haveref = true; goto case 0x74;
            case 0x74: DSP_PrepareDMA_Old(DSP_DMA_4, false, false); break;
            case 0x77: adpcm_haveref = true; goto case 0x76;
            case 0x76: DSP_PrepareDMA_Old(DSP_DMA_3, false, false); break;
            case 0x17: adpcm_haveref = true; goto case 0x16;
            case 0x16: DSP_PrepareDMA_Old(DSP_DMA_2, false, false); break;
            case 0x80:
                _mix.AddEvent(EV_RAISE, 1000.0f * (1 + dsp_in[0] + (dsp_in[1] << 8)) / freq, 0);
                break;
            case >= 0xb0 and <= 0xcf:
                if (!sb16) break;
                break;   // (SB16 generic DMA: not on this card)
            case 0xd5:
                if (!sb16) break;
                goto case 0xd0;
            case 0xd0:
                mode = MODE_DMA_PAUSE;
                _mix.RemoveEvents(EV_END_DMA);
                break;
            case 0xd1: DSP_SetSpeaker(true); break;
            case 0xd3: DSP_SetSpeaker(false); break;
            case 0xd8:
                if (!sb2above) break;
                DSP_FlushData();
                DSP_AddData(speaker ? (byte)0xff : (byte)0x00);
                break;
            case 0xd6:
                if (!sb16) break;
                goto case 0xd4;
            case 0xd4:
                if (mode == MODE_DMA_PAUSE) { mode = MODE_DMA_MASKED; Register(dma_chan, 1); }
                break;
            case 0xd9:
                if (!sb16) break;
                goto case 0xda;
            case 0xda:
                if (!sb2above) break;
                dma_autoinit = false;
                break;
            case 0xe0: DSP_FlushData(); DSP_AddData((byte)~dsp_in[0]); break;
            case 0xe1:
                DSP_FlushData();
                switch (type)
                {
                    case SBT_1: DSP_AddData(1); DSP_AddData(1); break;
                    case SBT_2: DSP_AddData(2); DSP_AddData(1); break;
                    case SBT_PRO1: DSP_AddData(3); DSP_AddData(0); break;
                    case SBT_PRO2: DSP_AddData(3); DSP_AddData(2); break;
                    case SBT_16: DSP_AddData(4); DSP_AddData(5); break;
                }
                break;
            case 0xe2:
                for (int i = 0; i < 8; i++) if (((dsp_in[0] >> i) & 1) != 0) e2_value += E2_incr_table[e2_count % 4, i];
                e2_value += E2_incr_table[e2_count % 4, 8];
                e2_count++;
                Register(hw_dma8, 2);
                break;
            case 0xe3:
                DSP_FlushData();
                foreach (char c in copyright_string) DSP_AddData((byte)c);
                DSP_AddData(0);   // i <= strlen: the terminating zero too
                break;
            case 0xe4: dsp_test_register = dsp_in[0]; break;
            case 0xe8: DSP_FlushData(); DSP_AddData(dsp_test_register); break;
            case 0xf2: SB_RaiseIRQ(false); break;
            case 0xf8: DSP_FlushData(); DSP_AddData(0); break;
            case 0x0f: DSP_AddData(0xff); break;
            default: break;   // unimplemented / unhandled commands are dropped
        }
        dsp_cmd = 0;
        dsp_cmd_len = 0;
        dsp_in_pos = 0;
    }

    private void DSP_DoWrite(byte val)
    {
        if (dsp_cmd == 0)
        {
            dsp_cmd = val;
            dsp_cmd_len = DSP_cmd_len[val];
            dsp_in_pos = 0;
            if (dsp_cmd_len == 0) DSP_DoCommand();
        }
        else
        {
            dsp_in[dsp_in_pos] = val;
            dsp_in_pos++;
            if (dsp_in_pos >= dsp_cmd_len) DSP_DoCommand();
        }
    }

    private byte DSP_ReadData()
    {
        if (dsp_out_used != 0)
        {
            read_data_last = dsp_out[dsp_out_pos];
            dsp_out_pos++;
            if (dsp_out_pos >= DSP_BUFSIZE) dsp_out_pos -= DSP_BUFSIZE;
            dsp_out_used--;
        }
        return read_data_last;
    }

    private static float CALCVOL(int v) => (float)Math.Pow(10.0f, (31 - v) * -1.3f / 20);

    private void CTMIXER_UpdateVolumes()
    {
        if (!mixer_enabled) return;
        _chan.SetVolume(mixer_master[0] / 31.0f * CALCVOL(mixer_dac[0]), mixer_master[1] / 31.0f * CALCVOL(mixer_dac[1]));
        // (the "FM" channel's volume would go to the AdLib: not wired, the AdLib keeps its own level)
    }

    private void CTMIXER_Reset()
    {
        mixer_fm[0] = mixer_fm[1] = mixer_dac[0] = mixer_dac[1] = 31;
        mixer_master[0] = mixer_master[1] = 31;
        CTMIXER_UpdateVolumes();
    }

    private void SETPROVOL(byte[] which, int val)
    {
        which[0] = (byte)(((val & 0xf0) >> 3) | (type == SBT_16 ? 1 : 3));
        which[1] = (byte)(((val & 0x0f) << 1) | (type == SBT_16 ? 1 : 3));
    }

    private byte MAKEPROVOL(byte[] which) => (byte)((((which[0] & 0x1e) << 3) | ((which[1] & 0x1e) >> 1)) & (type == SBT_16 ? 0xff : 0xee));

    private void DSP_ChangeStereo(bool stereo)
    {
        if (!dma_stereo && stereo)
        {
            _chan.SetFreq(Math.Max(1, freq / 2));
            dma_mul *= 2;
            dma_rate = (freq * dma_mul) >> SB_SH;
            dma_min = dma_rate * 3 / 1000;
        }
        else if (dma_stereo && !stereo)
        {
            _chan.SetFreq(Math.Max(1, freq));
            dma_mul /= 2;
            dma_rate = (freq * dma_mul) >> SB_SH;
            dma_min = dma_rate * 3 / 1000;
        }
        dma_stereo = stereo;
    }

    private void CTMIXER_Write(byte val)
    {
        switch (mixer_index)
        {
            case 0x00: CTMIXER_Reset(); break;
            case 0x02: SETPROVOL(mixer_master, (val & 0xf) | (val << 4)); CTMIXER_UpdateVolumes(); break;
            case 0x04: SETPROVOL(mixer_dac, val); CTMIXER_UpdateVolumes(); break;
            case 0x06: SETPROVOL(mixer_fm, (val & 0xf) | (val << 4)); CTMIXER_UpdateVolumes(); break;
            case 0x08: SETPROVOL(mixer_cda, (val & 0xf) | (val << 4)); break;
            case 0x0a:
                if (type == SBT_2) mixer_dac[0] = mixer_dac[1] = (byte)(((val & 0x6) << 2) | 3);
                else mixer_mic = (byte)(((val & 0x7) << 2) | (type == SBT_16 ? 1 : 3));
                break;
            case 0x0e:
                mixer_stereo = (val & 0x2) > 0;
                mixer_filtered = (val & 0x20) > 0;
                DSP_ChangeStereo(mixer_stereo);
                break;
            case 0x22: SETPROVOL(mixer_master, val); CTMIXER_UpdateVolumes(); break;
            case 0x26: SETPROVOL(mixer_fm, val); CTMIXER_UpdateVolumes(); break;
            case 0x28: SETPROVOL(mixer_cda, val); break;
            case 0x2e: SETPROVOL(mixer_lin, val); break;
            case >= 0x30 and <= 0x3a: break;   // SB16 only
            case 0x80:
                hw_irq = 0xff;
                if ((val & 0x1) != 0) hw_irq = 2;
                else if ((val & 0x2) != 0) hw_irq = 5;
                else if ((val & 0x4) != 0) hw_irq = 7;
                else if ((val & 0x8) != 0) hw_irq = 10;
                break;
            case 0x81:
                hw_dma8 = 0xff; hw_dma16 = 0xff;
                if ((val & 0x1) != 0) hw_dma8 = 0;
                else if ((val & 0x2) != 0) hw_dma8 = 1;
                else if ((val & 0x8) != 0) hw_dma8 = 3;
                if ((val & 0x20) != 0) hw_dma16 = 5;
                else if ((val & 0x40) != 0) hw_dma16 = 6;
                else if ((val & 0x80) != 0) hw_dma16 = 7;
                break;
            default:
                if ((type == SBT_PRO1 || type == SBT_PRO2) && mixer_index == 0x0c) mixer_unhandled[mixer_index] = val;
                break;
        }
    }

    private byte CTMIXER_Read()
    {
        switch (mixer_index)
        {
            case 0x00: return 0x00;
            case 0x02: return (byte)((mixer_master[1] >> 1) & 0xe);
            case 0x22: return MAKEPROVOL(mixer_master);
            case 0x04: return MAKEPROVOL(mixer_dac);
            case 0x06: return (byte)((mixer_fm[1] >> 1) & 0xe);
            case 0x08: return (byte)((mixer_cda[1] >> 1) & 0xe);
            case 0x0a: return type == SBT_2 ? (byte)(mixer_dac[0] >> 2) : (byte)((mixer_mic >> 2) & (type == SBT_16 ? 7 : 6));
            case 0x0e: return (byte)(0x11 | (mixer_stereo ? 0x02 : 0x00) | (mixer_filtered ? 0x20 : 0x00));
            case 0x26: return MAKEPROVOL(mixer_fm);
            case 0x28: return MAKEPROVOL(mixer_cda);
            case 0x2e: return MAKEPROVOL(mixer_lin);
            case >= 0x30 and <= 0x3a: return 0xa;
            case 0x80:
                switch (hw_irq) { case 2: return 0x1; case 5: return 0x2; case 7: return 0x4; case 10: return 0x8; }
                goto case 0x81;   // the original falls through into the DMA select
            case 0x81:
            {
                byte ret = 0;
                switch (hw_dma8) { case 0: ret |= 0x1; break; case 1: ret |= 0x2; break; case 3: ret |= 0x8; break; }
                switch (hw_dma16) { case 5: ret |= 0x20; break; case 6: ret |= 0x40; break; case 7: ret |= 0x80; break; }
                return ret;
            }
            case 0x82: return (byte)((irq_pending_8bit ? 0x1 : 0) | (irq_pending_16bit ? 0x2 : 0));
            default:
                if ((type == SBT_PRO1 || type == SBT_PRO2) && mixer_index == 0x0c) return mixer_unhandled[mixer_index];
                return 0xa;
        }
    }

    private int read_sb(int port)
    {
        switch (port - hw_base)
        {
            case 0x04: return mixer_index;
            case 0x05: return CTMIXER_Read();
            case 0x0a: return DSP_ReadData();
            case 0x0e:
                irq_pending_8bit = false;
                return dsp_out_used != 0 ? 0xff : 0x7f;
            case 0x0f:
                irq_pending_16bit = false;
                break;
            case 0x0c:
                if (dsp_state == DSP_S_NORMAL) { dsp_write_busy++; return (dsp_write_busy & 8) != 0 ? 0xff : 0x7f; }
                return 0xff;
            case 0x06: return 0xff;
        }
        return 0xff;
    }

    private void write_sb(int port, byte val)
    {
        switch (port - hw_base)
        {
            case 0x06: DSP_DoReset(val); break;
            case 0x0c: DSP_DoWrite(val); break;
            case 0x04: mixer_index = val; break;
            case 0x05: CTMIXER_Write(val); break;
        }
    }

    private void SBLASTER_CallBack(int len)
    {
        switch (mode)
        {
            case MODE_NONE:
            case MODE_DMA_PAUSE:
            case MODE_DMA_MASKED:
                _chan.AddSilence();
                break;
            case MODE_DAC:
                if (dac_used == 0) { mode = MODE_NONE; return; }
                _chan.AddStretched(dac_used, dac_data);
                dac_used = 0;
                break;
            case MODE_DMA:
                long l = (long)len * dma_mul;
                if ((l & SB_SH_MASK) != 0) l += 1 << SB_SH;
                l >>= SB_SH;
                if (l > dma_left) l = dma_left;
                GenerateDMASound((int)l);
                break;
        }
    }

    // ================= IPcSoundCard =================

    public bool In(int port, bool word, out int value)
    {
        value = 0xff;
        if (word) return false;
        int o = port - hw_base;
        if (o is >= 0 and <= 3 || o is 8 or 9) { value = (o & 1) == 0 ? _m.Opl.ReadStatus() : 0xff; return true; }   // the FM (the AdLib's chip)
        if (o is >= 4 and <= 0xf) { Advance(); value = read_sb(port); return true; }
        return false;
    }

    public bool Out(int port, bool word, int value)
    {
        if (word) return false;
        int o = port - hw_base;
        if (o is >= 0 and <= 3 || o is 8 or 9) { if ((o & 1) == 0) _m.Opl.WriteAddress((byte)value); else _m.Opl.WriteData((byte)value); return true; }
        if (o is >= 4 and <= 0xf) { Advance(); write_sb(port, (byte)value); return true; }
        return false;
    }

    public void Advance() => _mix.Advance();
    public int MixInto(short[] buffer, int count) => _mix.MixInto(buffer, count);
    public int Pending => _mix.Pending;
    public void Restart() => _mix.Restart();
    public void SaveOutput(BinaryWriter w) => _mix.SaveOutput(w);
    public void LoadOutput(BinaryReader r) => _mix.LoadOutput(r);

    public void Save(BinaryWriter w)
    {
        w.Write(1);
        _mix.Save(w);
        w.Write(freq); w.Write(dma_stereo); w.Write(dma_sign); w.Write(dma_autoinit); w.Write(dma_mode); w.Write(dma_rate); w.Write(dma_mul); w.Write(dma_total);
        w.Write(dma_left); w.Write(dma_min); w.Write(dma_remain_size); w.Write(dma_b8[0]); w.Write(dma_b8[1]); w.Write(dma_chan);
        w.Write(speaker); w.Write(midi); w.Write(time_constant); w.Write(mode); w.Write(irq_pending_8bit); w.Write(irq_pending_16bit);
        w.Write(dsp_state); w.Write(dsp_cmd); w.Write(dsp_cmd_len); w.Write(dsp_cmd_in_pos); w.Write(dsp_in); w.Write(dsp_in_pos);
        w.Write(dsp_out); w.Write(dsp_out_pos); w.Write(dsp_out_used); w.Write(dsp_test_register); w.Write(dsp_write_busy);
        foreach (var s in dac_data) w.Write(s);
        w.Write(dac_used); w.Write(dac_last);
        w.Write(mixer_index); w.Write(mixer_dac); w.Write(mixer_fm); w.Write(mixer_cda); w.Write(mixer_master); w.Write(mixer_lin); w.Write(mixer_mic);
        w.Write(mixer_stereo); w.Write(mixer_enabled); w.Write(mixer_filtered); w.Write(mixer_unhandled);
        w.Write(adpcm_reference); w.Write(adpcm_stepsize); w.Write(adpcm_haveref);
        w.Write(hw_base); w.Write(hw_irq); w.Write(hw_dma8); w.Write(hw_dma16); w.Write(e2_value); w.Write(e2_count); w.Write(read_data_last);
        int cbch = -1;
        for (int i = 0; i < 8; i++) if (_m.Dma[i].Callback == DmaCb) cbch = i;
        w.Write(cb_kind); w.Write(cbch);
    }

    public void Load(BinaryReader r)
    {
        if (r.ReadInt32() != 1) throw new InvalidDataException("Sound Blaster state version");
        _mix.Load(r);
        freq = r.ReadInt32(); dma_stereo = r.ReadBoolean(); dma_sign = r.ReadBoolean(); dma_autoinit = r.ReadBoolean(); dma_mode = r.ReadInt32(); dma_rate = r.ReadInt32(); dma_mul = r.ReadInt32(); dma_total = r.ReadInt32();
        dma_left = r.ReadInt32(); dma_min = r.ReadInt32(); dma_remain_size = r.ReadInt32(); dma_b8[0] = r.ReadByte(); dma_b8[1] = r.ReadByte(); dma_chan = r.ReadInt32();
        speaker = r.ReadBoolean(); midi = r.ReadBoolean(); time_constant = r.ReadByte(); mode = r.ReadInt32(); irq_pending_8bit = r.ReadBoolean(); irq_pending_16bit = r.ReadBoolean();
        dsp_state = r.ReadByte(); dsp_cmd = r.ReadByte(); dsp_cmd_len = r.ReadByte(); dsp_cmd_in_pos = r.ReadByte(); r.Read(dsp_in, 0, DSP_BUFSIZE); dsp_in_pos = r.ReadInt32();
        r.Read(dsp_out, 0, DSP_BUFSIZE); dsp_out_pos = r.ReadInt32(); dsp_out_used = r.ReadInt32(); dsp_test_register = r.ReadByte(); dsp_write_busy = r.ReadInt32();
        for (int i = 0; i < dac_data.Length; i++) dac_data[i] = r.ReadInt16();
        dac_used = r.ReadInt32(); dac_last = r.ReadInt16();
        mixer_index = r.ReadByte(); r.Read(mixer_dac, 0, 2); r.Read(mixer_fm, 0, 2); r.Read(mixer_cda, 0, 2); r.Read(mixer_master, 0, 2); r.Read(mixer_lin, 0, 2); mixer_mic = r.ReadByte();
        mixer_stereo = r.ReadBoolean(); mixer_enabled = r.ReadBoolean(); mixer_filtered = r.ReadBoolean(); r.Read(mixer_unhandled, 0, mixer_unhandled.Length);
        adpcm_reference = r.ReadByte(); adpcm_stepsize = r.ReadInt32(); adpcm_haveref = r.ReadBoolean();
        hw_base = r.ReadInt32(); hw_irq = r.ReadInt32(); hw_dma8 = r.ReadInt32(); hw_dma16 = r.ReadInt32(); e2_value = r.ReadInt32(); e2_count = r.ReadInt32(); read_data_last = r.ReadByte();
        cb_kind = r.ReadInt32(); int cbch = r.ReadInt32();
        foreach (var c in _m.Dma.Channels) if (c.Callback == DmaCb) c.Callback = null;
        if (cbch >= 0 && cb_kind != 0) _m.Dma[cbch].SetCallbackSilently(DmaCb);
    }
}
