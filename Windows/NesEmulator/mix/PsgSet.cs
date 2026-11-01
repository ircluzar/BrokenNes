using System;
using NesEmulator.Sega;

namespace NesEmulator.Mix
{
    /// <summary>
    /// One SN76489 of an enhanced sound chip's bank (the Master System / Game Gear enhanced chip's whole bank, the Genesis enhanced chip's PSG half) played from hub voices: a tone at a pitch (the PSG's
    /// 10-bit period, exact to its step; below its range, 109 Hz on a 3.58 MHz clock, whole octaves up and counted), the noise at a shift rate (white or periodic; one of the three fixed rates, or the exact
    /// rate through tone 3 when tone 3 is free), each at the 2 dB step nearest its level, and on a Game Gear the pan through the stereo port. Registers are written only when their value changes, so a held
    /// note keeps its phase. Allocation-free.
    /// </summary>
    internal sealed class PsgSet
    {
        public readonly Sn76489 Psg;
        private readonly double clockHz;
        private readonly bool stereo;
        private readonly int[] period = { -1, -1, -1 };
        private readonly int[] volume = { -1, -1, -1, -1 };
        private readonly bool[] low = new bool[3];
        private readonly bool[] toneBusy = new bool[3];
        private int noiseControl = -1, stereoMask = 0xFF;
        /// <summary>Notes moved whole octaves up into the PSG's range (once per note), and noise rates the PSG could only come near (tone 3 was busy).</summary>
        public long Transposed, NoiseCompromises;

        public PsgSet(double clockHz, int sampleRate, PsgVariant variant, bool stereo)
        {
            this.clockHz = clockHz; this.stereo = stereo;
            Psg = new Sn76489(clockHz, sampleRate, variant);
            for (int c = 0; c < 4; c++) SetVolume(c, 15);
        }

        private void SetPeriod(int ch, int n)
        {
            if (period[ch] == n) return;
            period[ch] = n;
            Psg.Write((byte)(0x80 | ch << 5 | (n & 0x0F)));
            Psg.Write((byte)((n >> 4) & 0x3F));
        }

        private void SetVolume(int ch, int att)
        {
            if (volume[ch] == att) return;
            volume[ch] = att;
            Psg.Write((byte)(0x90 | ch << 5 | att));
        }

        private void SetNoise(int control)
        {
            if (noiseControl == control) return;
            noiseControl = control;
            Psg.Write((byte)(0xE0 | control));      // reloads the shift register: only when the mode or rate changes
        }

        /// <summary>The pan of a channel on the Game Gear's stereo port (bit 4 + ch left, bit ch right); a mono set ignores it.</summary>
        private void Pan(int ch, float pan)
        {
            if (!stereo) return;
            int bit = ch;
            int mask = stereoMask & ~(0x11 << bit);
            if (pan < -0.33f) mask |= 0x10 << bit; else if (pan > 0.33f) mask |= 0x01 << bit; else mask |= 0x11 << bit;
            if (mask == stereoMask) return;
            stereoMask = mask; Psg.WriteStereo((byte)mask);
        }

        /// <summary>Tone <paramref name="ch"/> (0-2) at <paramref name="hz"/>, <paramref name="level"/> 0 to 1.</summary>
        public void Tone(int ch, double hz, float level, float pan)
        {
            toneBusy[ch] = true;
            int n = (int)Math.Round(clockHz / (32.0 * hz));
            if (n > 1023)
            {
                if (!low[ch]) { low[ch] = true; Transposed++; }
                while (n > 1023) n = (n + 1) >> 1;
            }
            else low[ch] = false;
            if (n < 2 || PitchGuard.Blocks(hz)) { SetVolume(ch, 15); return; }      // a period of 0 or 1 is not a note on the Sega part
            SetPeriod(ch, n);
            Pan(ch, pan);
            SetVolume(ch, HubVoiceChip.PsgAttenuation(level));
        }

        public void ToneOff(int ch) { toneBusy[ch] = false; low[ch] = false; SetVolume(ch, 15); }

        /// <summary>The noise at a shift rate of <paramref name="shiftHz"/>, white or periodic.</summary>
        public void Noise(double shiftHz, bool periodic, float level, float pan)
        {
            int fb = periodic ? 0 : 4;
            int rate = 0; double bd = double.MaxValue;
            for (int r = 0; r < 3; r++) { double d = Math.Abs(Math.Log(clockHz / (512.0 * (1 << r)) / Math.Max(1, shiftHz))); if (d < bd) { bd = d; rate = r; } }
            if (bd > 0.03 && !toneBusy[2])
            {
                SetPeriod(2, Math.Clamp((int)Math.Round(clockHz / (32.0 * Math.Max(1, shiftHz))), 1, 1023));      // the fourth rate follows tone 3, which no voice uses: the exact rate
                SetVolume(2, 15);
                rate = 3;
            }
            else if (bd > 0.03) NoiseCompromises++;
            SetNoise(fb | rate);
            Pan(3, pan);
            SetVolume(3, HubVoiceChip.PsgAttenuation(level));
        }

        public void NoiseOff() => SetVolume(3, 15);

        public void Silence() { for (int c = 0; c < 3; c++) ToneOff(c); NoiseOff(); }

        /// <summary>A raw byte to the PSG's port (address 0) or to the stereo port (address 1).</summary>
        public void Raw(ushort address, byte value)
        {
            if (address == 1) { stereoMask = value; Psg.WriteStereo(value); }
            else { Psg.Write(value); Array.Fill(period, -1); Array.Fill(volume, -1); noiseControl = -1; }
        }
    }
}
