// =====================================================================================================================================
// VirtuaNesApu - VirtuaNES 0.97's own 2A03 sound (APU_INTERNAL render side + APU.cpp's mixer), ported to C# (quick port, quirks kept).
//
// Source project : VirtuaNES (Norix)
// Version        : 0.97 (2002-2003), from the github.com/ChillMagic/VirtuaNES mirror (a modified/modernised mirror: ConfigWrapper,
//                  UTF-8 conversion; the sound code itself reads as Norix's), branch master, fetched 2026-11-01. No pristine 0.97
//                  archive was at hand.
// Files ported   : NES/ApuEX/APU_INTERNAL.cpp/.h (Reset, Setup, Write, Process, the Update/Render functions, SyncWrite4017 /
//                  UpdateFrame / Sync's frame-counter part that drives the private $4018 port), NES/ApuEX/APU_INTERFACE.h (APU_CLOCK,
//                  INT2FIX), NES/APU.cpp (Process: per-channel volumes, >>8, the 40 Hz DC-cut, clipping, 8-bit output; the write queue
//                  becomes "render up to the write's cycle, then apply"). The sync side's DMC IRQ / $4015 reads are APU_FIX's job.
// URL            : https://github.com/ChillMagic/VirtuaNES/tree/master/NES
// Copyright      : (C) 2001-2003 Norix
// License        : license not stated (VirtuaNES, Norix); documented exception.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Defaults kept : CCfgSound defaults as shipped (recalled from Config.cpp, not in the fetched files): nRate 22050, nBits 8,
//                 nFilterType 0 (no low-pass), every volume 100, bChangeTone off (the .vtd wavetable files are not ported),
//                 bDisableVolumeEffect off. NTSC.
// Quirks kept   :
//  - Not band-limited: squares are box-averaged over the sample (double maths, floor+0.5); triangle/noise step once per sample or
//    average whole steps when faster than the sample rate; the triangle holds its level when silenced.
//  - Its own frame sequencer: a tick every 14915/2 CPU cycles written to the private port $4018 (type 0..3); length/sweep on even
//    ticks, envelope/linear on every tick; in 5-step mode tick 3 just waits twice as long. $4017 with bit 7 ticks at once.
//  - Length counters are vbl_length*2 (halved ticks); square 1's sweep adds ~(f>>s), square 2 subtracts; a sweep rewrites the
//    rendered period permanently ($4002 keeps the swept high bits). Looping envelopes count (vol-1)&15.
//  - The "volume effect": triangle and noise are scaled by 256-(DMC level) (non-linear-mixer imitation).
//  - DMC: the sample address is $C000+v*64 (no $4000 base check), the length counted in bits, the byte read at render time, the
//    output slewed by at most 8 steps per sample ("petit noise cut"), and the last bit of a non-looping sample is dropped.
//  - Mixer: rect 0xF0, tri 0x130, noise 0xC0, DMC 0xF0, then >>8, a one-pole 40 Hz DC cut, clipped to 16 bits and then truncated to
//    8 bits (the 8-bit default). Sample clock = FrameCycles*60/12/nRate (81.04 CPU cycles; 1786840 Hz, not the CPU's 1789772.5).
// =====================================================================================================================================
using System;

namespace NesEmulator
{
    /// <summary>VirtuaNES 0.97's APU_INTERNAL (render side) with APU.cpp's mixer and VirtuaNesMmc5. Clocked in CPU cycles; 22050 Hz.</summary>
    public sealed class VirtuaNesApu
    {
        public const int SndRate = 22050;
        private const float APU_CLOCK = 1789772.5f;
        private const int FrameCycles = 1364 * 262; // NESCONFIG NTSC, master clocks per frame
        private const int RECTANGLE_VOL_SHIFT = 8, TRIANGLE_VOL_SHIFT = 9, DPCM_VOL_SHIFT = 8;
        private const int RECTANGLE_VOL = 0x0F0, TRIANGLE_VOL = 0x130, NOISE_VOL = 0x0C0, DPCM_VOL = 0x0F0, MMC5_VOL = 0x0F0;

        private static readonly int[] vbl_length = {
            5, 127, 10, 1, 19, 2, 40, 3, 80, 4, 30, 5, 7, 6, 13, 7,
            6, 8, 12, 9, 24, 10, 48, 11, 96, 12, 36, 13, 8, 14, 16, 15 };
        private static readonly int[] freq_limit = { 0x03FF, 0x0555, 0x0666, 0x071C, 0x0787, 0x07C1, 0x07E0, 0x07F0 };
        private static readonly int[] duty_lut = { 2, 4, 8, 12 };
        private static readonly int[] noise_freq = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        private static readonly int[] dpcm_cycles = { 428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 85, 72, 54 };

        private sealed class Rect
        {
            public readonly byte[] reg = new byte[4];
            public int enable, holdnote, volume, complement;
            public int phaseacc, freq, freqlimit, adder, duty, len_count, nowvolume;
            public int env_fixed, env_decay, env_count, env_vol;
            public int swp_on, swp_inc, swp_shift, swp_decay, swp_count;
        }
        private sealed class Tri
        {
            public readonly byte[] reg = new byte[4];
            public int enable, holdnote, counter_start, phaseacc, freq, len_count, lin_count, adder, nowvolume;
        }
        private sealed class Noise
        {
            public readonly byte[] reg = new byte[4];
            public int enable, holdnote, volume, xor_tap, shift_reg, phaseacc, freq, len_count, nowvolume, output;
            public int env_fixed, env_decay, env_count, env_vol;
        }
        private sealed class Dpcm
        {
            public readonly byte[] reg = new byte[4];
            public int enable, looping, cur_byte, dpcm_value, freq, phaseacc, output;
            public ushort address, cache_addr;
            public int dmalength, cache_dmalength, dpcm_output_real, dpcm_output_fake;
        }

        private Rect ch0 = new(), ch1 = new();
        private Tri ch2 = new();
        private Noise ch3 = new();
        private readonly Dpcm ch4 = new();
        private int reg4015;
        private int cycle_rate;

        // sync-side frame counter (only what drives $4018)
        private int FrameCycle, FrameCount, FrameIRQ;

        // APU.cpp
        private readonly Func<ushort, byte> nesRead;
        private readonly VirtuaNesMmc5 mmc5;
        private bool mmc5On;            // exsound_select & 0x08: set on the first MMC5 sound write
        private long totalCycles;
        private double elapsed_time;
        private readonly double sampleCycles = ((double)FrameCycles * 60.0 / 12.0) / SndRate;
        private double dcTmp;
        private const double dcCutoffTemp = 2.0 * 3.141592653579 * 40.0;
        public int ChannelMask = 0x1F;

        private const int RingSize = 16384;
        private readonly float[] ring = new float[RingSize];
        private int ringRead, ringWrite, ringCount;

        public VirtuaNesApu(Func<ushort, byte> readMem)
        {
            nesRead = readMem;
            mmc5 = new VirtuaNesMmc5(APU_CLOCK, SndRate);
            cycle_rate = (int)(APU_CLOCK * 65536.0f / 22050.0f);
            FrameIRQ = 0xC0;
            Reset();
        }

        private static int INT2FIX(int x) => x << 16;

        /// <summary>APU::Reset: APU_INTERNAL::Reset + APU_MMC5::Reset; the time base restarts at the current cycle.</summary>
        public void Reset()
        {
            ch0 = new Rect(); ch1 = new Rect(); ch2 = new Tri(); ch3 = new Noise();
            reg4015 = 0;
            ch0.complement = 0x00; ch1.complement = 0xFF;
            ch3.shift_reg = 0x4000;
            cycle_rate = (int)(APU_CLOCK * 65536.0f / (float)SndRate);
            for (int addr = 0x4000; addr <= 0x4010; addr++) InternalWrite((ushort)addr, 0);
            InternalWrite(0x4012, 0); InternalWrite(0x4013, 0); InternalWrite(0x4015, 0);
            FrameIRQ = 0xC0; FrameCycle = 0; FrameCount = 0;
            mmc5.Reset(APU_CLOCK, SndRate);
            elapsed_time = totalCycles;
        }

        // ---- CPU side ----
        public void Write(ushort addr, byte data)
        {
            if (addr < 0x4000 || addr > 0x401F) return;
            RenderUpTo(totalCycles);
            if (addr == 0x4017) SyncWrite4017(data);
            InternalWrite(addr, data);
        }

        public void WriteMmc5(ushort addr, byte data)
        {
            if (addr < 0x5000 || addr > 0x5015) return;
            RenderUpTo(totalCycles);
            mmc5On = true;
            mmc5.Write(addr, data);
        }

        private void SyncWrite4017(byte data)
        {
            FrameCycle = 0;
            FrameIRQ = data;
            FrameCount = 0;
            if ((data & 0x80) != 0) UpdateFrame();
            FrameCount = 1;
            FrameCycle = 14915;
        }

        private void UpdateFrame()
        {
            if (FrameCount == 3 && (FrameIRQ & 0x80) != 0) FrameCycle += 14915;
            InternalWrite(0x4018, (byte)FrameCount); // nes->Write( 0x4018 ) - queued at the current cycle
            FrameCount = (FrameCount + 1) & 3;
        }

        public void Clock(int cycles)
        {
            totalCycles += cycles;
            FrameCycle -= cycles * 2;
            if (FrameCycle <= 0)
            {
                RenderUpTo(totalCycles);
                FrameCycle += 14915;
                UpdateFrame();
            }
            RenderUpTo(totalCycles);
        }

        // ---- APU_INTERNAL::Write (render side) ----
        private void InternalWrite(ushort addr, byte data)
        {
            switch (addr)
            {
                case >= 0x4000 and <= 0x4007: WriteRectangle(addr < 0x4004 ? 0 : 1, addr, data); break;
                case >= 0x4008 and <= 0x400B: WriteTriangle(addr, data); break;
                case >= 0x400C and <= 0x400F: WriteNoise(addr, data); break;
                case >= 0x4010 and <= 0x4013: WriteDPCM(addr, data); break;
                case 0x4015:
                    reg4015 = data;
                    if ((data & 1) == 0) { ch0.enable = 0; ch0.len_count = 0; }
                    if ((data & 2) == 0) { ch1.enable = 0; ch1.len_count = 0; }
                    if ((data & 4) == 0) { ch2.enable = 0; ch2.len_count = 0; ch2.lin_count = 0; ch2.counter_start = 0; }
                    if ((data & 8) == 0) { ch3.enable = 0; ch3.len_count = 0; }
                    if ((data & 0x10) == 0) { ch4.enable = 0; ch4.dmalength = 0; }
                    else
                    {
                        ch4.enable = 0xFF;
                        if (ch4.dmalength == 0) { ch4.address = ch4.cache_addr; ch4.dmalength = ch4.cache_dmalength; ch4.phaseacc = 0; }
                    }
                    break;
                case 0x4018:
                    UpdateRectangle(ch0, data); UpdateRectangle(ch1, data); UpdateTriangle(data); UpdateNoise(data);
                    break;
            }
        }

        private void WriteRectangle(int no, ushort addr, byte data)
        {
            var ch = no == 0 ? ch0 : ch1;
            ch.reg[addr & 3] = data;
            switch (addr & 3)
            {
                case 0:
                    ch.holdnote = data & 0x20; ch.volume = data & 0x0F; ch.env_fixed = data & 0x10;
                    ch.env_decay = (data & 0x0F) + 1; ch.duty = duty_lut[data >> 6];
                    break;
                case 1:
                    ch.swp_on = data & 0x80; ch.swp_inc = data & 0x08; ch.swp_shift = data & 0x07;
                    ch.swp_decay = ((data >> 4) & 0x07) + 1; ch.freqlimit = freq_limit[data & 0x07];
                    break;
                case 2:
                    ch.freq = (ch.freq & ~0xFF) + data;
                    break;
                case 3:
                    ch.freq = ((data & 0x07) << 8) + (ch.freq & 0xFF);
                    ch.len_count = vbl_length[data >> 3] * 2;
                    ch.env_vol = 0x0F;
                    ch.env_count = (ch.env_decay + 1) & 0xFF;
                    ch.adder = 0;
                    if ((reg4015 & (1 << no)) != 0) ch.enable = 0xFF;
                    break;
            }
        }

        private void UpdateRectangle(Rect ch, int type)
        {
            if (ch.enable == 0 || ch.len_count <= 0) return;
            if ((type & 1) == 0)
            {
                if (ch.len_count != 0 && ch.holdnote == 0 && ch.len_count != 0) ch.len_count--;
                if (ch.swp_on != 0 && ch.swp_shift != 0)
                {
                    if (ch.swp_count != 0) ch.swp_count--;
                    if (ch.swp_count == 0)
                    {
                        ch.swp_count = ch.swp_decay;
                        if (ch.swp_inc != 0)
                        {
                            if (ch.complement == 0) ch.freq += ~(ch.freq >> ch.swp_shift);
                            else ch.freq -= ch.freq >> ch.swp_shift;
                        }
                        else ch.freq += ch.freq >> ch.swp_shift;
                    }
                }
            }
            if (ch.env_count != 0) ch.env_count--;
            if (ch.env_count == 0)
            {
                ch.env_count = ch.env_decay;
                if (ch.holdnote != 0) ch.env_vol = (ch.env_vol - 1) & 0x0F;
                else if (ch.env_vol != 0) ch.env_vol--;
            }
            if (ch.env_fixed == 0) ch.nowvolume = ch.env_vol << RECTANGLE_VOL_SHIFT;
        }

        private int RenderRectangle(Rect ch)
        {
            if (ch.enable == 0 || ch.len_count <= 0) return 0;
            if (ch.freq < 8 || (ch.swp_inc == 0 && ch.freq > ch.freqlimit)) return 0;
            if (ch.env_fixed != 0) ch.nowvolume = ch.volume << RECTANGLE_VOL_SHIFT;
            int volume = ch.nowvolume;
            double sample_weight = ch.phaseacc;
            if (sample_weight > cycle_rate) sample_weight = cycle_rate;
            double total = ch.adder < ch.duty ? sample_weight : -sample_weight;
            int freq = INT2FIX(ch.freq + 1);
            ch.phaseacc -= cycle_rate;
            while (ch.phaseacc < 0)
            {
                ch.phaseacc += freq;
                ch.adder = (ch.adder + 1) & 0x0F;
                sample_weight = freq;
                if (ch.phaseacc > 0) sample_weight -= ch.phaseacc;
                total += ch.adder < ch.duty ? sample_weight : -sample_weight;
            }
            return (int)Math.Floor(volume * total / cycle_rate + 0.5);
        }

        private void WriteTriangle(ushort addr, byte data)
        {
            ch2.reg[addr & 3] = data;
            switch (addr & 3)
            {
                case 0: ch2.holdnote = data & 0x80; break;
                case 2: ch2.freq = INT2FIX(((ch2.reg[3] & 0x07) << 8) + data + 1); break;
                case 3:
                    ch2.freq = INT2FIX(((data & 0x07) << 8) + ch2.reg[2] + 1);
                    ch2.len_count = vbl_length[data >> 3] * 2;
                    ch2.counter_start = 0x80;
                    if ((reg4015 & 4) != 0) ch2.enable = 0xFF;
                    break;
            }
        }

        private void UpdateTriangle(int type)
        {
            if (ch2.enable == 0) return;
            if ((type & 1) == 0 && ch2.holdnote == 0 && ch2.len_count != 0) ch2.len_count--;
            if (ch2.counter_start != 0) ch2.lin_count = ch2.reg[0] & 0x7F;
            else if (ch2.lin_count != 0) ch2.lin_count--;
            if (ch2.holdnote == 0 && ch2.lin_count != 0) ch2.counter_start = 0;
        }

        private int TriLevel()
        {
            ch2.adder = (ch2.adder + 1) & 0x1F;
            ch2.nowvolume = ch2.adder < 0x10 ? (ch2.adder & 0x0F) << TRIANGLE_VOL_SHIFT : (0x0F - (ch2.adder & 0x0F)) << TRIANGLE_VOL_SHIFT;
            return ch2.nowvolume;
        }

        private int RenderTriangle()
        {
            int vol = 256 - ((ch4.reg[1] & 0x01) + ch4.dpcm_value * 2);
            if (ch2.enable == 0 || ch2.len_count <= 0 || ch2.lin_count <= 0) return ch2.nowvolume * vol / 256;
            if (ch2.freq < INT2FIX(8)) return ch2.nowvolume * vol / 256;
            ch2.phaseacc -= cycle_rate;
            if (ch2.phaseacc >= 0) return ch2.nowvolume * vol / 256;
            if (ch2.freq > cycle_rate)
            {
                ch2.phaseacc += ch2.freq;
                TriLevel();
                return ch2.nowvolume * vol / 256;
            }
            int num_times = 0, total = 0;
            while (ch2.phaseacc < 0) { ch2.phaseacc += ch2.freq; total += TriLevel(); num_times++; }
            return total / num_times * vol / 256;
        }

        private void WriteNoise(ushort addr, byte data)
        {
            ch3.reg[addr & 3] = data;
            switch (addr & 3)
            {
                case 0:
                    ch3.holdnote = data & 0x20; ch3.volume = data & 0x0F; ch3.env_fixed = data & 0x10; ch3.env_decay = (data & 0x0F) + 1;
                    break;
                case 2:
                    ch3.freq = INT2FIX(noise_freq[data & 0x0F]);
                    ch3.xor_tap = (data & 0x80) != 0 ? 0x40 : 0x02;
                    break;
                case 3:
                    ch3.len_count = vbl_length[data >> 3] * 2;
                    ch3.env_vol = 0x0F;
                    ch3.env_count = (ch3.env_decay + 1) & 0xFF;
                    if ((reg4015 & 8) != 0) ch3.enable = 0xFF;
                    break;
            }
        }

        private void UpdateNoise(int type)
        {
            if (ch3.enable == 0 || ch3.len_count <= 0) return;
            if (ch3.holdnote == 0 && (type & 1) == 0 && ch3.len_count != 0) ch3.len_count--;
            if (ch3.env_count != 0) ch3.env_count--;
            if (ch3.env_count == 0)
            {
                ch3.env_count = ch3.env_decay;
                if (ch3.holdnote != 0) ch3.env_vol = (ch3.env_vol - 1) & 0x0F;
                else if (ch3.env_vol != 0) ch3.env_vol--;
            }
            if (ch3.env_fixed == 0) ch3.nowvolume = ch3.env_vol << RECTANGLE_VOL_SHIFT;
        }

        private int NoiseShiftreg(int xor_tap)
        {
            int bit0 = ch3.shift_reg & 1;
            int bit14 = (ch3.shift_reg & xor_tap) != 0 ? bit0 ^ 1 : bit0;
            ch3.shift_reg >>= 1;
            ch3.shift_reg |= bit14 << 14;
            return bit0 ^ 1;
        }

        private int RenderNoise()
        {
            if (ch3.enable == 0 || ch3.len_count <= 0) return 0;
            if (ch3.env_fixed != 0) ch3.nowvolume = ch3.volume << RECTANGLE_VOL_SHIFT;
            int vol = 256 - ((ch4.reg[1] & 0x01) + ch4.dpcm_value * 2);
            ch3.phaseacc -= cycle_rate;
            if (ch3.phaseacc >= 0) return ch3.output * vol / 256;
            if (ch3.freq > cycle_rate)
            {
                ch3.phaseacc += ch3.freq;
                ch3.output = NoiseShiftreg(ch3.xor_tap) != 0 ? ch3.nowvolume : -ch3.nowvolume;
                return ch3.output * vol / 256;
            }
            int num_times = 0, total = 0;
            while (ch3.phaseacc < 0)
            {
                ch3.phaseacc += ch3.freq;
                ch3.output = NoiseShiftreg(ch3.xor_tap) != 0 ? ch3.nowvolume : -ch3.nowvolume;
                total += ch3.output;
                num_times++;
            }
            return total / num_times * vol / 256;
        }

        private void WriteDPCM(ushort addr, byte data)
        {
            ch4.reg[addr & 3] = data;
            switch (addr & 3)
            {
                case 0: ch4.freq = INT2FIX(dpcm_cycles[data & 0x0F]); ch4.looping = data & 0x40; break;
                case 1: ch4.dpcm_value = (data & 0x7F) >> 1; break;
                case 2: ch4.cache_addr = (ushort)(0xC000 + (ushort)(data << 6)); break;
                case 3: ch4.cache_dmalength = ((data << 4) + 1) << 3; break;
            }
        }

        private int RenderDPCM()
        {
            if (ch4.dmalength != 0)
            {
                ch4.phaseacc -= cycle_rate;
                while (ch4.phaseacc < 0)
                {
                    ch4.phaseacc += ch4.freq;
                    if ((ch4.dmalength & 7) == 0)
                    {
                        ch4.cur_byte = nesRead(ch4.address);
                        ch4.address = ch4.address == 0xFFFF ? (ushort)0x8000 : (ushort)(ch4.address + 1);
                    }
                    if (--ch4.dmalength == 0)
                    {
                        if (ch4.looping != 0) { ch4.address = ch4.cache_addr; ch4.dmalength = ch4.cache_dmalength; }
                        else { ch4.enable = 0; break; }
                    }
                    if ((ch4.cur_byte & (1 << ((ch4.dmalength & 7) ^ 7))) != 0) { if (ch4.dpcm_value < 0x3F) ch4.dpcm_value++; }
                    else if (ch4.dpcm_value > 1) ch4.dpcm_value--;
                }
            }
            // "petit noise cut" (TEST)
            ch4.dpcm_output_real = ((ch4.reg[1] & 0x01) + ch4.dpcm_value * 2) - 0x40;
            if (Math.Abs(ch4.dpcm_output_real - ch4.dpcm_output_fake) <= 8)
            {
                ch4.dpcm_output_fake = ch4.dpcm_output_real;
                ch4.output = ch4.dpcm_output_real << DPCM_VOL_SHIFT;
            }
            else
            {
                ch4.dpcm_output_fake += ch4.dpcm_output_real > ch4.dpcm_output_fake ? 8 : -8;
                ch4.output = ch4.dpcm_output_fake << DPCM_VOL_SHIFT;
            }
            return ch4.output;
        }

        // ---- APU::Process ----
        private void RenderUpTo(long now)
        {
            while (elapsed_time < now)
            {
                int m = ChannelMask;
                int output = 0;
                // every channel is rendered (state advances) even when muted, like vol[]=0 in the original
                int r0 = RenderRectangle(ch0), r1 = RenderRectangle(ch1), t = RenderTriangle(), n = RenderNoise(), d = RenderDPCM();
                if ((m & 1) != 0) output += r0 * RECTANGLE_VOL;
                if ((m & 2) != 0) output += r1 * RECTANGLE_VOL;
                if ((m & 4) != 0) output += t * TRIANGLE_VOL;
                if ((m & 8) != 0) output += n * NOISE_VOL;
                if ((m & 16) != 0) output += d * DPCM_VOL;
                if (mmc5On)
                {
                    output += mmc5.Process(0) * MMC5_VOL;
                    output += mmc5.Process(1) * MMC5_VOL;
                    output += mmc5.Process(2) * MMC5_VOL;
                }
                output >>= 8;
                // 40 Hz DC cut
                double cutoff = dcCutoffTemp / SndRate;
                double inp = output;
                double outp = inp - dcTmp;
                dcTmp = dcTmp + cutoff * outp;
                output = (int)outp;
                if (output > 0x7FFF) output = 0x7FFF; else if (output < -0x8000) output = -0x8000;
                // nBits = 8: (output>>8)^0x80
                float s = (output >> 8) / 128f;
                ring[ringWrite] = s;
                ringWrite = (ringWrite + 1) & (RingSize - 1);
                if (ringCount < RingSize) ringCount++; else ringRead = (ringRead + 1) & (RingSize - 1);
                elapsed_time += sampleCycles;
            }
        }

        // ---- output queue ----
        public int Queued => ringCount;
        public void ClearOutput() { ringRead = ringWrite = ringCount = 0; }
        public int Read(Span<float> dest)
        {
            int n = Math.Min(dest.Length, ringCount);
            for (int i = 0; i < n; i++) { dest[i] = ring[ringRead]; ringRead = (ringRead + 1) & (RingSize - 1); }
            ringCount -= n;
            return n;
        }
    }
}
