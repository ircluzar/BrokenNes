using System;

namespace NesEmulator
{
    // Lightweight MMC5 Audio adapted from BizHawk's MMC5Audio, refit for BrokenNes mixing model.
    // - Two pulse channels with envelope/length/sweep behavior
    // - Simple PCM write/IRQ path ($5010/$5011)
    // - Exposes current mixed output as a float for the active APU to add during MixAndStore()
    // Notes:
    // - This is an approximation suitable for audible feedback; exact analog mixing levels can be tuned later.
    public class MMC5Audio
    {
        private readonly Action<bool> _raiseIrq;

        private class Pulse
        {
            // regs
            public int V; // volume/period for envelope
            public int T; // timer (11-bit)
            public int L; // length index
            public int D; // duty (2 bits)
            public bool LenCntDisable; // loop envelope
            public bool ConstantVolume;
            public bool Enable;
            // envelope
            private bool estart;
            private int etime;
            private int ecount;
            // length
            private static readonly int[] lenlookup =
            {
                10,254, 20,  2, 40,  4, 80,  6, 160,  8, 60, 10, 14, 12, 26, 14,
                12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30
            };
            private int length;
            // pulse
            private int sequence;
            private static readonly int[,] sequencelookup =
            {
                {0,0,0,0,0,0,0,1},
                {0,0,0,0,0,0,1,1},
                {0,0,0,0,1,1,1,1},
                {1,1,1,1,1,1,0,0}
            };
            private int clock;
            public int Output; // 0..15

            public void Write0(byte val)
            {
                V = val & 15;
                ConstantVolume = (val & 0x10) != 0;
                LenCntDisable = (val & 0x20) != 0;
                D = val >> 6;
                // start envelope
                estart = true;
            }
            public void Write2(byte val) { T = (T & 0x700) | val; }
            public void Write3(byte val)
            {
                T = (T & 0xFF) | ((val << 8) & 0x700);
                L = val >> 3;
                estart = true;
                if (Enable) length = lenlookup[L];
                sequence = 0;
            }
            public void SetEnable(bool val)
            {
                Enable = val;
                if (!Enable) length = 0;
            }
            public bool ReadLength() => length > 0;

            public void ClockFrame()
            {
                // envelope
                if (estart)
                {
                    estart = false; ecount = 15; etime = V;
                }
                else
                {
                    etime--;
                    if (etime < 0)
                    {
                        etime = V;
                        if (ecount > 0) ecount--; else if (LenCntDisable) ecount = 15;
                    }
                }
                // length
                if (Enable && !LenCntDisable && length > 0) length--;
            }

            public void Clock()
            {
                clock--;
                if (clock < 0)
                {
                    clock = T * 2 + 1;
                    sequence--; if (sequence < 0) sequence += 8;
                    int sequenceval = sequencelookup[D, sequence];
                    int newvol = 0;
                    if (sequenceval > 0 && length > 0)
                        newvol = ConstantVolume ? V : ecount;
                    Output = newvol; // expose amplitude directly
                }
            }
        }

        private readonly Pulse[] _pulse = new Pulse[2] { new Pulse(), new Pulse() };

        // PCM / IRQ
        private const int FrameReload = 7458; // approx
        private int frame;
        private bool PCMRead;
        private bool PCMEnableIRQ;
        private bool PCMIRQTriggered;
        private byte PCMVal; // current latched value
        private byte PCMNextVal; // next value (written by $5011 or ROM trigger)

        public MMC5Audio(Action<bool> raiseIrq)
        {
            _raiseIrq = raiseIrq;
        }

        // === Register I/O ===
        // addr: CPU absolute 0x5000..0x5015
        public void WriteExp(ushort addr, byte val)
        {
            switch (addr)
            {
                case 0x5000: _pulse[0].Write0(val); break;
                case 0x5002: _pulse[0].Write2(val); break;
                case 0x5003: _pulse[0].Write3(val); break;
                case 0x5004: _pulse[1].Write0(val); break;
                case 0x5006: _pulse[1].Write2(val); break;
                case 0x5007: _pulse[1].Write3(val); break;
                case 0x5010:
                    PCMRead = (val & 0x01) != 0;
                    PCMEnableIRQ = (val & 0x80) != 0;
                    _raiseIrq(PCMEnableIRQ && PCMIRQTriggered);
                    break;
                case 0x5011:
                    if (!PCMRead) WritePCM(val);
                    break;
                case 0x5015:
                    _pulse[0].SetEnable((val & 0x01) != 0);
                    _pulse[1].SetEnable((val & 0x02) != 0);
                    break;
                default:
                    break;
            }
        }

        public byte Read5015()
        {
            byte ret = 0;
            if (_pulse[0].ReadLength()) ret |= 1;
            if (_pulse[1].ReadLength()) ret |= 2;
            return ret;
        }
        public byte Read5010()
        {
            byte ret = 0;
            if (PCMEnableIRQ && PCMIRQTriggered) ret |= 0x80;
            PCMIRQTriggered = false; _raiseIrq(PCMEnableIRQ && PCMIRQTriggered);
            return ret;
        }
        public byte Peek5010()
        {
            byte ret = 0;
            if (PCMEnableIRQ && PCMIRQTriggered) ret |= 0x80;
            return ret;
        }

        // Call when PRG ROM $8000-$BFFF is read (MMC5 PCM read mode)
        public void ReadROMTrigger(byte val)
        {
            if (PCMRead) WritePCM(val);
        }

        private void WritePCM(byte val)
        {
            if (val == 0)
            {
                PCMIRQTriggered = true;
            }
            else
            {
                PCMIRQTriggered = false;
                PCMNextVal = val;
            }
            _raiseIrq(PCMEnableIRQ && PCMIRQTriggered);
        }

        // === Runtime ===
        public void Step(int cpuCycles)
        {
            for (int i = 0; i < cpuCycles; i++)
            {
                _pulse[0].Clock();
                _pulse[1].Clock();
                frame++;
                if (frame == FrameReload)
                {
                    frame = 0; _pulse[0].ClockFrame(); _pulse[1].ClockFrame();
                }
                if (PCMNextVal != PCMVal)
                {
                    // Latch immediately; final mixing uses PCMVal as DC-like level
                    PCMVal = PCMNextVal;
                }
            }
        }

        // Approximate mixed output as float in [-1,1] range.
        public float GetCurrentSample()
        {
            int pSum = _pulse[0].Output + _pulse[1].Output; // 0..30
            // Use standard NES pulse mixer curve to approximate loudness of the two MMC5 squares.
            double pulseMix = pSum == 0 ? 0.0 : 95.88 / (8128.0 / pSum + 100.0);
            // Scale PCMVal (0..255 or 0..127 depending on writer) conservatively to avoid overpowering
            double pcm = (PCMVal & 0x7F) / 127.0 * 0.12; // gentle contribution
            // Soft clip to keep within [-1,1]
            double mixed = pulseMix + pcm;
            mixed = Math.Tanh(mixed * 2.2);
            return (float)mixed * 0.8f; // align roughly with APU output gain
        }
    }
}
