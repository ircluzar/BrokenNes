// =====================================================================================================================================
// VirtuaNesMmc5 - VirtuaNES 0.97's MMC5 expansion sound (the render side of APU_MMC5), ported to C# (quick port, quirks kept).
//
// Source project : VirtuaNES (Norix)
// Version        : 0.97 (2002-2003), from the github.com/ChillMagic/VirtuaNES mirror (a modified/modernised mirror: ConfigWrapper,
//                  UTF-8 conversion; the sound code itself reads as Norix's), branch master, fetched 2026-11-01
// Files ported   : NES/ApuEX/APU_MMC5.cpp, APU_MMC5.h (Reset, Setup, Write, Process, RectangleRender). The sync side (SyncWrite /
//                  SyncRead / Sync: the $5015 status length counters) is not ported - BrokenNes2's MMC5Audio answers those reads.
// URL            : https://github.com/ChillMagic/VirtuaNES/tree/master/NES/ApuEX
// Copyright      : (C) 2002 Norix
// License        : license not stated (VirtuaNES, Norix); documented exception.
// This is a documented exception to the repository's license policy - see THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
// Quirks kept   :
//  - Envelope and length counter run per OUTPUT SAMPLE (decay_lut / vbl_lut scaled by nRate/60*5, env_phase -= 20, vbl_length -= 5),
//    not from a frame sequencer; the envelope counts env_vol UP and plays 0x0F-env_vol.
//  - No sweep; periods < 8 are silent; square amplitude volume<<8, nearest-step averaging only when the period is under one sample.
//  - The PCM is reg5011<<6 always (the $5010 read/write mode bit is ignored), unipolar.
// =====================================================================================================================================
namespace NesEmulator
{
    /// <summary>VirtuaNES 0.97's APU_MMC5 render side: two squares and the $5011 PCM, one value per channel per output sample.</summary>
    public sealed class VirtuaNesMmc5
    {
        private const int RECTANGLE_VOL_SHIFT = 8;
        private const int DAOUT_VOL_SHIFT = 6;

        private static readonly int[] vbl_length = {
            5, 127, 10, 1, 19, 2, 40, 3, 80, 4, 30, 5, 7, 6, 13, 7,
            6, 8, 12, 9, 24, 10, 48, 11, 96, 12, 36, 13, 8, 14, 16, 15 };
        private static readonly int[] duty_lut = { 2, 4, 8, 12 };
        private readonly int[] decay_lut = new int[16];
        private readonly int[] vbl_lut = new int[32];

        private sealed class Rect
        {
            public readonly byte[] reg = new byte[4];
            public byte enable;
            public int vbl_length, phaseacc, freq, output_vol;
            public byte fixed_envelope, holdnote, volume, env_vol;
            public int env_phase, env_decay, adder, duty_flip;
        }

        private Rect ch0 = new(), ch1 = new();
        private byte reg5010, reg5011, reg5015;
        private int cycle_rate;

        public VirtuaNesMmc5(float fClock, int nRate) { Reset(fClock, nRate); }

        private static int INT2FIX(int x) => x << 16;

        public void Reset(float fClock, int nRate)
        {
            ch0 = new Rect(); ch1 = new Rect();
            reg5010 = reg5011 = reg5015 = 0;
            Setup(fClock, nRate);
            for (int addr = 0x5000; addr <= 0x5015; addr++) Write((ushort)addr, 0);
        }

        public void Setup(float fClock, int nRate)
        {
            cycle_rate = (int)(fClock * 65536.0f / (float)nRate);
            int samples = (int)((float)nRate / 60.0f);
            for (int i = 0; i < 16; i++) decay_lut[i] = (i + 1) * samples * 5;
            for (int i = 0; i < 32; i++) vbl_lut[i] = vbl_length[i] * samples * 5;
        }

        public void Write(ushort addr, byte data)
        {
            switch (addr)
            {
                case 0x5000: case 0x5004:
                {
                    var ch = addr == 0x5000 ? ch0 : ch1;
                    ch.reg[0] = data;
                    ch.volume = (byte)(data & 0x0F);
                    ch.holdnote = (byte)(data & 0x20);
                    ch.fixed_envelope = (byte)(data & 0x10);
                    ch.env_decay = decay_lut[data & 0x0F];
                    ch.duty_flip = duty_lut[data >> 6];
                    break;
                }
                case 0x5001: ch0.reg[1] = data; break;
                case 0x5005: ch1.reg[1] = data; break;
                case 0x5002: case 0x5006:
                {
                    var ch = addr == 0x5002 ? ch0 : ch1;
                    ch.reg[2] = data;
                    ch.freq = INT2FIX(((ch.reg[3] & 0x07) << 8) + data + 1);
                    break;
                }
                case 0x5003: case 0x5007:
                {
                    var ch = addr == 0x5003 ? ch0 : ch1;
                    ch.reg[3] = data;
                    ch.vbl_length = vbl_lut[data >> 3];
                    ch.env_vol = 0;
                    ch.freq = INT2FIX(((data & 0x07) << 8) + ch.reg[2] + 1);
                    if ((reg5015 & (addr == 0x5003 ? 0x01 : 0x02)) != 0) ch.enable = 0xFF;
                    break;
                }
                case 0x5010: reg5010 = data; break;
                case 0x5011: reg5011 = data; break;
                case 0x5015:
                    reg5015 = data;
                    if ((reg5015 & 0x01) != 0) ch0.enable = 0xFF; else { ch0.enable = 0; ch0.vbl_length = 0; }
                    if ((reg5015 & 0x02) != 0) ch1.enable = 0xFF; else { ch1.enable = 0; ch1.vbl_length = 0; }
                    break;
            }
        }

        public int Process(int channel) => channel switch
        {
            0 => RectangleRender(ch0),
            1 => RectangleRender(ch1),
            2 => reg5011 << DAOUT_VOL_SHIFT,
            _ => 0,
        };

        private int RectangleRender(Rect ch)
        {
            if (ch.enable == 0 || ch.vbl_length <= 0) return 0;
            if (ch.holdnote == 0) ch.vbl_length -= 5;
            ch.env_phase -= 5 * 4;
            while (ch.env_phase < 0)
            {
                ch.env_phase += ch.env_decay;
                if (ch.holdnote != 0) ch.env_vol = (byte)((ch.env_vol + 1) & 0x0F);
                else if (ch.env_vol < 0x0F) ch.env_vol++;
            }
            if (ch.freq < INT2FIX(8)) return 0;
            int volume = ch.fixed_envelope != 0 ? ch.volume : 0x0F - ch.env_vol;
            int output = volume << RECTANGLE_VOL_SHIFT;
            ch.phaseacc -= cycle_rate;
            if (ch.phaseacc >= 0)
            {
                ch.output_vol = ch.adder < ch.duty_flip ? output : -output;
                return ch.output_vol;
            }
            if (ch.freq > cycle_rate)
            {
                ch.phaseacc += ch.freq;
                ch.adder = (ch.adder + 1) & 0x0F;
                ch.output_vol = ch.adder < ch.duty_flip ? output : -output;
            }
            else
            {
                int num_times = 0, total = 0;
                while (ch.phaseacc < 0)
                {
                    ch.phaseacc += ch.freq;
                    ch.adder = (ch.adder + 1) & 0x0F;
                    if (ch.adder < ch.duty_flip) total += output; else total -= output;
                    num_times++;
                }
                ch.output_vol = total / num_times;
            }
            return ch.output_vol;
        }
    }
}
