using System;
using System.IO;

namespace NesEmulator.Gb;

// =====================================================================================================================
//  TGB Dual (Hii) Game Boy sound, ported to C#.
//
//  Source: TGB Dual - Gameboy Emulator, libretro fork https://github.com/libretro/tgbdual-libretro (master, fetched
//  2026-11-01): gb_core/apu.cpp (apu / apu_snd: write, read, process, update, render, *_produce, mrand), the apu_stat /
//  apu_snd declarations in gb_core/gb.h, and the frontend's per-frame render call in libretro/dmy_renderer.cpp
//  (44,100 Hz, one render() per 70,224-clock frame, the sample count error-diffused).
//  Copyright (C) 2001 Hii. License: GPL-2.0-or-later. This is a documented exception to the repository's license
//  policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (the user's call: quick and dirty first, polish later). The original's shortcuts are kept on purpose:
//   - register writes are queued with their clock and replayed into a second state copy (stat_cpy) while a whole
//     frame's samples are rendered at once; at most ONE queued write is applied per output sample, the leftovers at
//     the end of the frame
//   - length / envelope / sweep run in update() "every 1/256 s" (16,384 clocks), but render() counts from 0 each
//     frame, so a 70,224-clock frame gets 5 updates (~299 Hz); envelope = every 4*n updates, sweep = every 2*n, all
//     from one shared counter that the live (register-read) path also advances
//   - no frame-sequencer phase, no DAC checks, no trigger on zero-volume kill; channel 2 has no sweep; sweep never
//     silences; NR52 status masks channels 1 and 2 by the WAVE channel's volume (wav_vol)
//   - squares: naive 8-step duty tables at +-10000 (duty 75% = the 25% table inverted), phase step clamped to
//     0xFFFF; NR13/NR14 freq above 65 kHz gives a DC 15000; wave: point-sampled 4-bit nibbles, freq above 65 kHz gives
//     a DC level from the first nibble; wave volume shift table {0,4,2,1}/10 (0/100/50/25 %, louder than squares)
//   - noise: LFSR stepped "freq/44100" times per sample, several steps per sample are averaged (the integer divide
//     keeps a box-filter); 7/15-bit width swap masks the register instead of resetting it; NR22 with shift 12+ (>>6==3)
//     kills the channel
//   - trigger quirks: square 1/noise reset the phase only when the channel was already silent (never: playing is set
//     first), the wave channel never resets its position
//   - reset copies the boot wave pattern to mem+20 (decimal: $FF24-$FF33), not $FF30
//   - output: L/R are written swapped (the author's "3.5-inch bay speaker" fix), clamp +-32767, no master volume
//     beyond the 0-7 NR50 scale (/8); register reads return the raw byte written, no masks
//  Left out: the echo and low-pass options (off by default in the libretro core) and dual-GB mode.
// =====================================================================================================================

/// <summary>TGB Dual's apu_stat (one copy is live for register reads, one is replayed for rendering).</summary>
public sealed class TgbdStat
{
    public bool sq1_playing; public int sq1_sw_time, sq1_sw_dir, sq1_sw_shift, sq1_len, sq1_init_len, sq1_type;
    public int sq1_vol, sq1_init_vol, sq1_env_dir, sq1_env_speed, sq1_freq, sq1_init_freq, sq1_hold;
    public bool sq2_playing; public int sq2_len, sq2_init_len, sq2_type, sq2_vol, sq2_init_vol, sq2_env_dir, sq2_env_speed;
    public int sq2_freq, sq2_init_freq, sq2_hold;
    public bool wav_playing; public int wav_vol, wav_freq, wav_init_freq, wav_init_len, wav_len, wav_hold;
    public bool noi_playing; public int noi_len, noi_init_len, noi_vol, noi_init_vol, noi_env_dir, noi_env_speed;
    public int noi_freq, noi_init_freq, noi_hold, noi_step;
    public int master_enable;
    public int[,] ch_enable = new int[4, 2];
    public int[] master_vol = new int[2];
    public int[] ch_on = new int[4];
    public int wav_enable;

    public TgbdStat Clone()
    {
        var c = (TgbdStat)MemberwiseClone();
        c.ch_enable = (int[,])ch_enable.Clone(); c.master_vol = (int[])master_vol.Clone(); c.ch_on = (int[])ch_on.Clone();
        return c;
    }

    /// <summary>Visit every field (save: f writes and returns; load: f returns the read value).</summary>
    public void Io(Func<int, int> f)
    {
        static int B(bool b) => b ? 1 : 0;
        sq1_playing = f(B(sq1_playing)) != 0; sq1_sw_time = f(sq1_sw_time); sq1_sw_dir = f(sq1_sw_dir); sq1_sw_shift = f(sq1_sw_shift);
        sq1_len = f(sq1_len); sq1_init_len = f(sq1_init_len); sq1_type = f(sq1_type); sq1_vol = f(sq1_vol); sq1_init_vol = f(sq1_init_vol);
        sq1_env_dir = f(sq1_env_dir); sq1_env_speed = f(sq1_env_speed); sq1_freq = f(sq1_freq); sq1_init_freq = f(sq1_init_freq); sq1_hold = f(sq1_hold);
        sq2_playing = f(B(sq2_playing)) != 0; sq2_len = f(sq2_len); sq2_init_len = f(sq2_init_len); sq2_type = f(sq2_type); sq2_vol = f(sq2_vol);
        sq2_init_vol = f(sq2_init_vol); sq2_env_dir = f(sq2_env_dir); sq2_env_speed = f(sq2_env_speed); sq2_freq = f(sq2_freq);
        sq2_init_freq = f(sq2_init_freq); sq2_hold = f(sq2_hold);
        wav_playing = f(B(wav_playing)) != 0; wav_vol = f(wav_vol); wav_freq = f(wav_freq); wav_init_freq = f(wav_init_freq);
        wav_init_len = f(wav_init_len); wav_len = f(wav_len); wav_hold = f(wav_hold);
        noi_playing = f(B(noi_playing)) != 0; noi_len = f(noi_len); noi_init_len = f(noi_init_len); noi_vol = f(noi_vol); noi_init_vol = f(noi_init_vol);
        noi_env_dir = f(noi_env_dir); noi_env_speed = f(noi_env_speed); noi_freq = f(noi_freq); noi_init_freq = f(noi_init_freq);
        noi_hold = f(noi_hold); noi_step = f(noi_step);
        master_enable = f(master_enable);
        for (int i = 0; i < 4; i++) { ch_enable[i, 0] = f(ch_enable[i, 0]); ch_enable[i, 1] = f(ch_enable[i, 1]); ch_on[i] = f(ch_on[i]); }
        master_vol[0] = f(master_vol[0]); master_vol[1] = f(master_vol[1]);
        wav_enable = f(wav_enable);
    }
}

/// <summary>TGB Dual's apu + apu_snd: <see cref="Write"/> with the current clock, <see cref="Render"/> a frame.</summary>
public sealed class TgbdApu
{
    public const int Rate = 44100;
    public const int ClocksPerFrame = 70224;
    private const int CLOKS_PER_INTERVAL = 16384;
    private const bool Speed = false;              // base-clock units: TGB's double-speed doubling cancels out

    private struct Que { public int adr; public byte dat; public int clock; }

    public TgbdStat stat = new(), stat_cpy = new();
    private readonly Que[] write_que = new Que[0x10000];
    private int que_count;
    private int bef_clock;
    public readonly byte[] mem = new byte[0x100];   // $FF10-based
    private readonly bool[] b_enable = { true, true, true, true };
    private readonly bool b_lowpass = false;
    private readonly bool cgb;

    private uint sq1_cur_pos, sq2_cur_pos, wav_cur_pos, noi_cur_pos;
    private uint sq1_cur_sample, sq2_cur_sample, wav_cur_pos2;
    private byte wav_bef_sample, wav_cur_sample;
    private int noi_cur_sample = 10000;
    private int mrand_shift_reg = 0x7f, mrand_bef_degree;
    private int update_counter;
    private int render_now_time;
    private readonly int[] render_bef_sample_l = new int[5], render_bef_sample_r = new int[5];
    private int write_bef_clock, write_clocks;
    private bool write_bef_clock_init;

    public TgbdApu(bool cgb) { this.cgb = cgb; Reset(); }

    public void Reset()
    {
        que_count = 0; bef_clock = 0;
        stat = new TgbdStat();
        for (int i = 0; i < 4; i++) { stat.ch_enable[i, 0] = stat.ch_enable[i, 1] = 1; stat.ch_on[i] = 1; }
        stat.master_enable = 1;
        stat.master_vol[0] = stat.master_vol[1] = 7;
        stat_cpy = stat.Clone();

        sq1_cur_pos = sq2_cur_pos = wav_cur_pos = noi_cur_pos = 0;
        sq1_cur_sample = sq2_cur_sample = 0; wav_cur_pos2 = 0;
        wav_bef_sample = wav_cur_sample = 0;
        noi_cur_sample = 10000; mrand_shift_reg = 0x7f; mrand_bef_degree = 0;
        update_counter = 0; render_now_time = 0;
        Array.Clear(render_bef_sample_l); Array.Clear(render_bef_sample_r);
        write_bef_clock = 0; write_clocks = 0; write_bef_clock_init = false;

        byte[] gb_init_wav = { 0x06, 0xFE, 0x0E, 0x7F, 0x00, 0xFF, 0x58, 0xDF, 0x00, 0xEC, 0x00, 0xBF, 0x0C, 0xED, 0x03, 0xF7 };
        byte[] gbc_init_wav = { 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF };
        Array.Copy(cgb ? gbc_init_wav : gb_init_wav, 0, mem, 20, 16);   // sic: mem+20 (decimal) = $FF24
    }

    /// <summary>apu::read, reg = low byte ($10-$3F).</summary>
    public byte Read(int reg)
    {
        int adr = 0xFF00 | reg;
        if (adr == 0xff26)
            return (byte)(stat.master_enable == 0 ? 0x00 :
                (0x80 | ((stat.sq1_playing && stat.wav_vol != 0) ? 1 : 0) |
                    ((stat.sq2_playing && stat.wav_vol != 0) ? 2 : 0) |
                    ((stat.wav_enable != 0 && stat.wav_playing && stat.wav_vol != 0) ? 4 : 0) |
                    ((stat.noi_playing && stat.noi_vol != 0) ? 8 : 0)));
        return mem[adr - 0xff10];
    }

    /// <summary>apu::write.</summary>
    public void Write(int reg, byte dat, int clock)
    {
        int adr = 0xFF00 | reg;
        if (!write_bef_clock_init) { write_bef_clock = clock; write_bef_clock_init = true; }
        mem[adr - 0xFF10] = dat;
        write_que[que_count].adr = adr; write_que[que_count].dat = dat; write_que[que_count++].clock = clock;
        if (que_count >= 0x10000) que_count = 0xffff;
        process(stat, adr, dat);
        if (write_bef_clock > clock) write_bef_clock = clock;
        write_clocks += clock - write_bef_clock;
        while (write_clocks > CLOKS_PER_INTERVAL * (Speed ? 2 : 1))
        {
            update(stat);
            write_clocks -= CLOKS_PER_INTERVAL * (Speed ? 2 : 1);
        }
        write_bef_clock = clock;
    }

    /// <summary>Shift every stored clock down by <paramref name="d"/> (keeps the int clock from wrapping).</summary>
    public void Rebase(int d)
    {
        bef_clock -= d; write_bef_clock -= d; render_now_time -= d;
        for (int i = 0; i < que_count; i++) write_que[i].clock -= d;
    }

    private void process(TgbdStat stat, int adr, byte dat)
    {
        int[] tb = { 0, 4, 2, 1 };
        int[] mul_t = { 2, 1, 1, 1, 1, 1, 1, 1 };
        int[] div_t = { 1, 1, 2, 3, 4, 5, 6, 7 };
        int[] div_t2 = { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 1, 1 };

        mem[adr - 0xFF10] = dat;

        switch (adr)
        {
            case 0xFF10:
                stat.sq1_sw_time = (dat >> 4) & 7; stat.sq1_sw_dir = (dat >> 3) & 1; stat.sq1_sw_shift = dat & 7;
                break;
            case 0xFF11:
                stat.sq1_type = (dat >> 6) & 3; stat.sq1_init_len = 64 - (dat & 0x3F); stat.sq1_len = stat.sq1_init_len;
                break;
            case 0xFF12:
                stat.sq1_init_vol = (dat >> 4) & 0xF; stat.sq1_vol = stat.sq1_init_vol;
                stat.sq1_env_dir = (dat >> 3) & 1; stat.sq1_env_speed = dat & 7;
                break;
            case 0xFF13:
                stat.sq1_init_freq &= 0x700; stat.sq1_init_freq |= dat; stat.sq1_freq = stat.sq1_init_freq;
                break;
            case 0xFF14:
                stat.sq1_init_freq &= 0xFF; stat.sq1_init_freq |= ((dat & 7) << 8); stat.sq1_freq = stat.sq1_init_freq;
                stat.sq1_hold = (dat >> 6) & 1;
                if ((dat & 0x80) != 0)
                {
                    stat.sq1_playing = true;
                    stat.sq1_vol = stat.sq1_init_vol;
                    stat.sq1_len = stat.sq1_init_len;
                    if ((!stat.sq1_playing) || (stat.sq1_vol == 0)) sq1_cur_pos = 0;
                }
                break;
            case 0xFF16:
                stat.sq2_type = (dat >> 6) & 3; stat.sq2_init_len = 64 - (dat & 0x3F); stat.sq2_len = stat.sq2_init_len;
                break;
            case 0xFF17:
                stat.sq2_init_vol = (dat >> 4) & 0xF; stat.sq2_vol = stat.sq2_init_vol;
                stat.sq2_env_dir = (dat >> 3) & 1; stat.sq2_env_speed = dat & 7;
                break;
            case 0xFF18:
                stat.sq2_init_freq &= 0x700; stat.sq2_init_freq |= dat; stat.sq2_freq = stat.sq2_init_freq;
                break;
            case 0xFF19:
                stat.sq2_init_freq &= 0xFF; stat.sq2_init_freq |= ((dat & 7) << 8); stat.sq2_freq = stat.sq2_init_freq;
                stat.sq2_hold = (dat >> 6) & 1;
                if ((dat & 0x80) != 0)
                {
                    if ((!stat.sq2_playing) || (stat.sq2_vol == 0)) sq2_cur_pos = 0;
                    stat.sq2_playing = true;
                    stat.sq2_vol = stat.sq2_init_vol;
                    stat.sq2_len = stat.sq2_init_len;
                }
                break;
            case 0xFF1A:
                stat.wav_enable = (dat & 0x80) != 0 ? 1 : 0;
                break;
            case 0xFF1B:
                stat.wav_init_len = (256 - dat); stat.wav_len = stat.wav_init_len;
                stat.wav_playing = (stat.wav_len != 0 && dat != 0) || stat.wav_hold == 0;
                break;
            case 0xFF1C:
                stat.wav_vol = tb[(dat >> 5) & 3];
                break;
            case 0xFF1D:
                stat.wav_freq &= 0x700; stat.wav_freq |= dat;
                break;
            case 0xFF1E:
                stat.wav_freq &= 0xFF; stat.wav_freq |= ((dat & 7) << 8);
                stat.wav_hold = (dat >> 6) & 1;
                if ((dat & 0x80) != 0)
                {
                    stat.wav_len = stat.wav_init_len;
                    stat.wav_playing = true;
                    if (!stat.wav_playing) wav_cur_pos = 0;
                }
                break;
            case 0xFF20:
                stat.noi_init_len = 64 - (dat & 0x3F); stat.noi_len = stat.noi_init_len;
                if (stat.noi_len == 0) stat.noi_playing = false;
                break;
            case 0xFF21:
                stat.noi_init_vol = (dat >> 4) & 0x0F; stat.noi_vol = stat.noi_init_vol;
                stat.noi_env_dir = (dat >> 3) & 1; stat.noi_env_speed = dat & 7;
                if (stat.noi_vol == 0) stat.noi_playing = false;
                break;
            case 0xFF22:
                stat.noi_init_freq = 4194304 * mul_t[dat & 7] / div_t[dat & 7] / div_t2[(dat >> 4) & 15] / 8;
                stat.noi_freq = stat.noi_init_freq;
                stat.noi_step = (dat & 8) != 0 ? 7 : 15;
                if ((dat >> 6) == 3) stat.noi_playing = false;
                break;
            case 0xFF23:
                stat.noi_hold = (dat >> 6) & 1;
                if ((dat & 0x80) != 0)
                {
                    stat.noi_playing = true;
                    stat.noi_len = stat.noi_init_len;
                    stat.noi_vol = stat.noi_init_vol;
                    if ((!stat.noi_playing) || (stat.noi_vol == 0)) noi_cur_pos = 0;
                }
                break;
            case 0xFF24:
                stat.master_vol[0] = (dat & 7); stat.master_vol[1] = ((dat >> 4) & 7);
                break;
            case 0xFF25:
                for (int c = 0; c < 4; c++) { stat.ch_enable[c, 0] = (dat >> c) & 1; stat.ch_enable[c, 1] = (dat >> (c + 4)) & 1; }
                break;
            case 0xFF26:
                stat.master_enable = (dat & 0x80) != 0 ? 1 : 0;
                for (int c = 0; c < 4; c++) stat.ch_on[c] = (dat >> c) & 1;
                break;
        }
    }

    private static readonly int[,] sq_wav_dat = {
        { 0, 1, 0, 0, 0, 0, 0, 0 },
        { 1, 1, 0, 0, 0, 0, 0, 0 },
        { 1, 1, 1, 1, 0, 0, 0, 0 },
        { 0, 0, 1, 1, 1, 1, 1, 1 },
    };

    private short sq_produce(int type, int freq, ref uint cur_pos, ref uint cur_sample)
    {
        if (freq > 65000) return 15000;
        if (freq == 0) return 0;
        short ret = (short)(sq_wav_dat[type & 3, cur_sample] * 20000 - 10000);
        uint cur_freq = ((freq * 8) > 0x10000) ? 0xffffu : (uint)(freq * 8);
        cur_pos += (cur_freq << 16) / 44100;
        if ((cur_pos & 0xffff0000) != 0)
        {
            cur_sample = (cur_sample + (cur_pos >> 16)) & 7;
            cur_pos &= 0xffff;
        }
        return ret;
    }

    private short wav_produce(int freq, bool interpolation)
    {
        if (freq > 65000) return (short)((mem[0x20] >> 4) * 4000 - 30000);
        if (freq == 0) return 0;
        short ret = interpolation
            ? (short)(((wav_cur_sample * 2500 - 15000) * (long)wav_cur_pos + (wav_bef_sample * 2500 - 15000) * (long)(0x10000 - wav_cur_pos)) / 0x10000)
            : (short)(wav_cur_sample * 2500 - 15000);
        uint cur_freq = (freq > 0x10000) ? 0xffffu : (uint)freq;
        wav_cur_pos += (cur_freq << 16) / 44100;
        if ((wav_cur_pos & 0xffff0000) != 0)
        {
            wav_bef_sample = wav_cur_sample;
            wav_cur_pos2 = (wav_cur_pos2 + (wav_cur_pos >> 16)) & 31;
            wav_cur_sample = (wav_cur_pos2 & 1) != 0 ? (byte)(mem[0x20 + wav_cur_pos2 / 2] & 0xf) : (byte)(mem[0x20 + wav_cur_pos2 / 2] >> 4);
            wav_cur_pos &= 0xffff;
        }
        return ret;
    }

    private uint mrand(uint degree)
    {
        int xor_reg = 0;
        degree = (degree == 7) ? 0u : 1u;
        if (mrand_bef_degree != (int)degree)
        {
            mrand_shift_reg &= (degree != 0 ? 0x7fff : 0x7f);
            if (mrand_shift_reg == 0) mrand_shift_reg = degree != 0 ? 0x7fff : 0x7f;
        }
        mrand_bef_degree = (int)degree;
        int masked = mrand_shift_reg & 3;
        while (masked != 0) { xor_reg ^= masked & 0x01; masked >>= 1; }
        if (xor_reg != 0) mrand_shift_reg |= (degree != 0 ? 0x8000 : 0x80);
        else mrand_shift_reg &= ~(degree != 0 ? 0x8000 : 0x80);
        mrand_shift_reg >>= 1;
        return (uint)mrand_shift_reg;
    }

    private short noi_produce(TgbdStat stat, int freq)
    {
        if (freq == 0) return 0;
        short ret = (short)noi_cur_sample;
        noi_cur_pos += (uint)freq;
        int sc = 0;
        while (noi_cur_pos > 44100)
        {
            if (sc == 0) noi_cur_sample = (mrand((uint)stat.noi_step) & 1) != 0 ? 12000 : -10000;
            else noi_cur_sample += (mrand((uint)stat.noi_step) & 1) != 0 ? 12000 : -10000;
            noi_cur_pos -= 44100;
            sc++;
        }
        if (sc > 0) noi_cur_sample /= sc;
        return ret;
    }

    private void update(TgbdStat stat)
    {
        if (stat.sq1_playing && stat.master_enable != 0)
        {
            if (stat.sq1_env_speed != 0 && (update_counter % (4 * stat.sq1_env_speed) == 0))
            {
                stat.sq1_vol += (stat.sq1_env_dir != 0 ? 1 : -1);
                if (stat.sq1_vol < 0) stat.sq1_vol = 0;
                if (stat.sq1_vol > 15) stat.sq1_vol = 15;
            }
            if (stat.sq1_sw_time != 0 && stat.sq1_sw_shift != 0 && (update_counter % (2 * stat.sq1_sw_time) == 0))
            {
                if (stat.sq1_sw_dir != 0) stat.sq1_freq = stat.sq1_freq - (stat.sq1_freq >> stat.sq1_sw_shift);
                else stat.sq1_freq = stat.sq1_freq + (stat.sq1_freq >> stat.sq1_sw_shift);
            }
            if (stat.sq1_hold != 0 && stat.sq1_len != 0)
            {
                stat.sq1_len--;
                if (stat.sq1_len <= 0) stat.sq1_playing = false;
            }
        }
        if (stat.sq2_playing && stat.master_enable != 0)
        {
            if (stat.sq2_env_speed != 0 && (update_counter % (4 * stat.sq2_env_speed) == 0))
            {
                stat.sq2_vol += (stat.sq2_env_dir != 0 ? 1 : -1);
                if (stat.sq2_vol < 0) stat.sq2_vol = 0;
                if (stat.sq2_vol > 15) stat.sq2_vol = 15;
            }
            if (stat.sq2_hold != 0 && stat.sq2_len != 0)
            {
                stat.sq2_len--;
                if (stat.sq2_len <= 0) stat.sq2_playing = false;
            }
        }
        if (stat.wav_playing && stat.master_enable != 0)
        {
            if (stat.wav_hold != 0 && stat.wav_len != 0)
            {
                stat.wav_len--;
                if (stat.wav_len <= 0) stat.wav_playing = false;
            }
        }
        if (stat.noi_playing && stat.master_enable != 0)
        {
            if (stat.noi_env_speed != 0 && (update_counter % (4 * stat.noi_env_speed) == 0))
            {
                stat.noi_vol += (stat.noi_env_dir != 0 ? 1 : -1);
                if (stat.noi_vol < 0) stat.noi_vol = 0;
                if (stat.noi_vol > 15) stat.noi_vol = 15;
            }
            if (stat.noi_hold != 0 && stat.noi_len != 0)
            {
                stat.noi_len--;
                if (stat.noi_len <= 0) stat.noi_playing = false;
            }
        }
        update_counter++;
    }

    /// <summary>apu_snd::render: <paramref name="sample"/> stereo pairs (swapped L/R, as TGB writes them) covering
    /// bef_clock..<paramref name="now_clock"/>.</summary>
    public void Render(short[] buf, int sample, int now_clock)
    {
        var stat = stat_cpy;
        int cur = 0, update_count = 0;
        Array.Clear(buf, 0, sample * 2);

        for (int i = 0; i < sample; i++)
        {
            render_now_time = (int)(bef_clock + (long)(now_clock - bef_clock) * i / sample);

            if ((cur != 0x10000) && (render_now_time > write_que[cur].clock) && (que_count != 0))
            {
                process(stat, write_que[cur].adr, write_que[cur].dat);
                cur++;
                if (cur >= que_count) cur = 0x10000;
            }

            int tmp_l = 0, tmp_r = 0, tmp;
            if (stat.master_enable != 0)
            {
                if (b_enable[0] && stat.sq1_playing)
                {
                    tmp = sq_produce(stat.sq1_type, 131072 / (2048 - (stat.sq1_freq & 0x7FF)), ref sq1_cur_pos, ref sq1_cur_sample) * stat.sq1_vol / 20;
                    if (stat.ch_enable[0, 0] != 0) tmp_l += tmp * stat.master_vol[0] / 8;
                    if (stat.ch_enable[0, 1] != 0) tmp_r += tmp * stat.master_vol[1] / 8;
                }
                if (b_enable[1] && stat.sq2_playing)
                {
                    tmp = sq_produce(stat.sq2_type, 131072 / (2048 - (stat.sq2_freq & 0x7FF)), ref sq2_cur_pos, ref sq2_cur_sample) * stat.sq2_vol / 20;
                    if (stat.ch_enable[1, 0] != 0) tmp_l += tmp * stat.master_vol[0] / 8;
                    if (stat.ch_enable[1, 1] != 0) tmp_r += tmp * stat.master_vol[1] / 8;
                }
                if (b_enable[2] && stat.wav_playing)
                {
                    tmp = wav_produce((65536 / (2048 - (stat.wav_freq & 0x7FF))) * 32, false) * stat.wav_vol / 10 * stat.wav_enable;
                    if (stat.ch_enable[2, 0] != 0) tmp_l += tmp * stat.master_vol[0] / 8;
                    if (stat.ch_enable[2, 1] != 0) tmp_r += tmp * stat.master_vol[1] / 8;
                }
                if (b_enable[3] && stat.noi_playing)
                {
                    tmp = noi_produce(stat, stat.noi_freq) * stat.noi_vol / 20;
                    if (stat.ch_enable[3, 0] != 0) tmp_l += tmp * stat.master_vol[0] / 8;
                    if (stat.ch_enable[3, 1] != 0) tmp_r += tmp * stat.master_vol[1] / 8;
                }
            }
            if (b_lowpass)
            {
                for (int k = 4; k > 0; k--) { render_bef_sample_l[k] = render_bef_sample_l[k - 1]; render_bef_sample_r[k] = render_bef_sample_r[k - 1]; }
                render_bef_sample_l[0] = tmp_l; render_bef_sample_r[0] = tmp_r;
                tmp_l = (render_bef_sample_l[4] + render_bef_sample_l[3] * 2 + render_bef_sample_l[2] * 8 + render_bef_sample_l[1] * 2 + render_bef_sample_l[0]) / 14;
                tmp_r = (render_bef_sample_r[4] + render_bef_sample_r[3] * 2 + render_bef_sample_r[2] * 8 + render_bef_sample_r[1] * 2 + render_bef_sample_r[0]) / 14;
            }
            tmp_l = Math.Clamp(tmp_l, -32767, 32767);
            tmp_r = Math.Clamp(tmp_r, -32767, 32767);
            buf[i * 2] = (short)tmp_r;        // sic: swapped
            buf[i * 2 + 1] = (short)tmp_l;

            while ((long)update_count * CLOKS_PER_INTERVAL * (Speed ? 2 : 1) < render_now_time - bef_clock)
            {
                update(stat);
                update_count++;
            }
        }
        while (cur < que_count)
        {
            process(stat, write_que[cur].adr, write_que[cur].dat);
            cur++;
        }
        que_count = 0;
        bef_clock = now_clock;
    }

    public void Save(BinaryWriter w)
    {
        stat.Io(v => { w.Write(v); return v; });
        stat_cpy.Io(v => { w.Write(v); return v; });
        w.Write(mem);
        w.Write(que_count);
        for (int i = 0; i < que_count; i++) { w.Write(write_que[i].adr); w.Write(write_que[i].dat); w.Write(write_que[i].clock); }
        w.Write(bef_clock);
        w.Write(sq1_cur_pos); w.Write(sq2_cur_pos); w.Write(wav_cur_pos); w.Write(noi_cur_pos);
        w.Write(sq1_cur_sample); w.Write(sq2_cur_sample); w.Write(wav_cur_pos2);
        w.Write(wav_bef_sample); w.Write(wav_cur_sample); w.Write(noi_cur_sample);
        w.Write(mrand_shift_reg); w.Write(mrand_bef_degree); w.Write(update_counter); w.Write(render_now_time);
        for (int i = 0; i < 5; i++) { w.Write(render_bef_sample_l[i]); w.Write(render_bef_sample_r[i]); }
        w.Write(write_bef_clock); w.Write(write_clocks); w.Write(write_bef_clock_init);
    }

    public void Load(BinaryReader r)
    {
        stat.Io(_ => r.ReadInt32());
        stat_cpy.Io(_ => r.ReadInt32());
        r.Read(mem, 0, mem.Length);
        que_count = r.ReadInt32();
        for (int i = 0; i < que_count; i++) { write_que[i].adr = r.ReadInt32(); write_que[i].dat = r.ReadByte(); write_que[i].clock = r.ReadInt32(); }
        bef_clock = r.ReadInt32();
        sq1_cur_pos = r.ReadUInt32(); sq2_cur_pos = r.ReadUInt32(); wav_cur_pos = r.ReadUInt32(); noi_cur_pos = r.ReadUInt32();
        sq1_cur_sample = r.ReadUInt32(); sq2_cur_sample = r.ReadUInt32(); wav_cur_pos2 = r.ReadUInt32();
        wav_bef_sample = r.ReadByte(); wav_cur_sample = r.ReadByte(); noi_cur_sample = r.ReadInt32();
        mrand_shift_reg = r.ReadInt32(); mrand_bef_degree = r.ReadInt32(); update_counter = r.ReadInt32(); render_now_time = r.ReadInt32();
        for (int i = 0; i < 5; i++) { render_bef_sample_l[i] = r.ReadInt32(); render_bef_sample_r[i] = r.ReadInt32(); }
        write_bef_clock = r.ReadInt32(); write_clocks = r.ReadInt32(); write_bef_clock_init = r.ReadBoolean();
    }
}
