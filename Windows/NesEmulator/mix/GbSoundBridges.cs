using System;
using System.Collections.Generic;
using System.IO;
using NesEmulator.Mix;
using NesEmulator.Gb;

namespace NesEmulator.Mix
{
    /// <summary>
    /// The Game Boy family as the bridges see it: sound-chip ids -> factories. "GB" = the real APU_GB,
    /// "NES" = <see cref="GbApuOnNes"/> (the game's sound replayed on any NES APU core, which itself may be the
    /// SNES bridge, so a Game Boy game can end up on the S-DSP).
    /// </summary>
    public static class GbCores
    {
        private static readonly Dictionary<string, Func<GbModel, IGbApu>> apus = new(StringComparer.OrdinalIgnoreCase)
        {
            ["GB"] = m => new APU_GB(m),
            ["NES"] = m => new GbApuOnNes(m),
            ["GBS"] = m => new APU_GBS(m),
        };
        public static IEnumerable<string> ApuIds => apus.Keys;
        public static IGbApu CreateApu(string id, GbModel model) =>
            apus.TryGetValue(id, out var f) ? f(model) : throw new ArgumentException($"No Game Boy APU '{id}' (have: {string.Join(", ", apus.Keys)})");
    }

    /// <summary>
    /// MIX LAB: a Game Boy sound chip whose sound comes from a NES APU core (<see cref="MixConfig.GbBackNesApu"/>).
    /// The real APU_GB stays in front so register reads, NR52 status, length counters and envelopes behave; its own
    /// output is discarded. About 960 times a second its four channels are replayed on the NES channels:
    /// pulse 1/2 -> NES pulse 1/2 (the duty cycles are the same four), wave -> triangle (the waveform is lost, the pitch
    /// kept), noise -> noise (nearest NES period; the 7-bit mode maps to the NES short mode). Stereo panning is dropped.
    /// </summary>
    public sealed class GbApuOnNes : IGbApu, NesEmulator.Sega.IHubVoiceSource
    {
        private readonly APU_GB front;
        private readonly NesEmulator.IAPU back;
        private readonly string backId;
        private readonly int[] cache = new int[0x18];
        private double nesAcc; private int sinceSync;
        private readonly Queue<float> queue = new();
        public long Syncs;
        public long MidiNotes;

        private readonly bool driveOnly;

        public GbApuOnNes(GbModel model) : this(model, null) { }

        /// <summary><paramref name="hostApu"/>: drive a NES APU that a running NES already steps and plays (the Game Boy
        /// cartridge on a NES) - only its registers are written. Null: a private NES APU core, stepped and drained here.</summary>
        public GbApuOnNes(GbModel model, NesEmulator.IAPU? hostApu)
        {
            front = new APU_GB(model);
            backId = hostApu != null ? "host" : MixConfig.GbBackNesApu;
            back = hostApu ?? new NesApuHost(backId).Apu;
            driveOnly = hostApu != null;
            // APU_WF plays through the system MIDI synth, not samples: count its notes so a silent WAV is not read as silence.
            if (back is NesEmulator.APU_WF wf) wf.NoteEvent += _ => MidiNotes++;
            Array.Fill(cache, -1);
            N(0x4017, 0x40); N(0x4015, 0x0F); N(0x4001, 0x08); N(0x4005, 0x08);
        }

        public string CoreName => $"NES:{back.CoreName}+GB";
        public int SampleRate => back.GetSampleRate();
        public void ResetPostBoot() => front.ResetPostBoot();
        public void FrameSequencerStep() => front.FrameSequencerStep();
        public byte ReadRegister(int reg) => front.ReadRegister(reg);
        public void WriteRegister(int reg, byte v) => front.WriteRegister(reg, v);
        public void SaveState(BinaryWriter w) => front.SaveState(w);
        public void LoadState(BinaryReader r) => front.LoadState(r);

        private void N(ushort a, byte v) { if (cache[a - 0x4000] == v) return; cache[a - 0x4000] = v; back.WriteAPURegister(a, v); }

        public void Tick(int tCycles)
        {
            front.Tick(tCycles);
            if (!driveOnly)
            {
                nesAcc += tCycles * (1789773.0 / 4194304.0);
                int c = (int)nesAcc;
                if (c > 0) { nesAcc -= c; back.Step(c); }
            }
            if ((sinceSync += tCycles) >= 4369) { sinceSync = 0; Sync(); }
        }

        private void Sync()
        {
            Syncs++;
            for (int i = 0; i < 2; i++)
            {
                var ch = front.Channel(i);
                ushort b = (ushort)(0x4000 + i * 4);
                int duty = ch.Duty & 3;
                if (ch.Enabled && ch.DacOn && ch.Volume > 0 && ch.Period < 2048 && !PitchGuard.Blocks(131072.0 / (2048 - ch.Period)))
                {
                    double hz = 131072.0 / (2048 - ch.Period);
                    int t = Math.Clamp((int)Math.Round(1789773.0 / (16 * hz) - 1), 8, 2047);
                    N(b, (byte)(duty << 6 | 0x30 | ch.Volume));
                    N((ushort)(b + 2), (byte)t);
                    N((ushort)(b + 3), (byte)(0x08 | (t >> 8)));
                }
                else N(b, (byte)(duty << 6 | 0x30));
            }
            {
                var ch = front.Channel(2);
                if (ch.Enabled && ch.DacOn && ch.Volume > 0 && ch.Period < 2048 && !PitchGuard.Blocks(65536.0 / (2048 - ch.Period)))
                {
                    double hz = 65536.0 / (2048 - ch.Period);
                    int t = Math.Clamp((int)Math.Round(1789773.0 / (32 * hz) - 1), 2, 2047);
                    N(0x4008, 0xFF); N(0x400A, (byte)t); N(0x400B, (byte)(0x08 | (t >> 8)));
                }
                else N(0x4008, 0x80);
            }
            {
                var ch = front.Channel(3);
                if (ch.Enabled && ch.DacOn && ch.Volume > 0)
                {
                    double r = ch.NoiseDivisorCode == 0 ? 0.5 : ch.NoiseDivisorCode;
                    double hz = 262144.0 / (r * Math.Pow(2, ch.NoiseShift));
                    int best = 0; double bd = double.MaxValue;
                    for (int k = 0; k < 16; k++) { double d = Math.Abs(Math.Log(1789773.0 / NesChannelModel.NoisePeriods[k] / hz)); if (d < bd) { bd = d; best = k; } }
                    N(0x400C, (byte)(0x30 | ch.Volume)); N(0x400E, (byte)((ch.NoiseShortMode ? 0x80 : 0) | best)); N(0x400F, 0x08);
                }
                else N(0x400C, 0x30);
            }
        }

        /// <summary>The Game Boy's four voices as the hub's voice list (tools and tests): pulse 1 and 2, the wave channel (a triangle on the NES), the noise; the level is the
        /// channel's volume (0 to 1), the pan from NR51.</summary>
        public int DescribeVoices(Span<NesEmulator.Sega.HubVoice> voices)
        {
            int nr51 = front.ReadRegister(0x25), n = Math.Min(4, voices.Length);
            for (int i = 0; i < n; i++)
            {
                var ch = front.Channel(i);
                bool on = ch.Enabled && ch.DacOn && ch.Volume > 0;
                float pan = NesEmulator.Sega.HubVoice.PanOf((nr51 >> (4 + i) & 1) != 0, (nr51 >> i & 1) != 0);
                double hz = i switch
                {
                    0 or 1 => ch.Period < 2048 ? 131072.0 / (2048 - ch.Period) : 0,
                    2 => ch.Period < 2048 ? 65536.0 / (2048 - ch.Period) : 0,
                    _ => 262144.0 / ((ch.NoiseDivisorCode == 0 ? 0.5 : ch.NoiseDivisorCode) * Math.Pow(2, ch.NoiseShift)),
                };
                var kind = i < 2 ? NesEmulator.Sega.HubVoiceKind.Pulse : i == 2 ? NesEmulator.Sega.HubVoiceKind.Triangle : NesEmulator.Sega.HubVoiceKind.Noise;
                voices[i] = new NesEmulator.Sega.HubVoice(kind, on ? hz : 0, on ? ch.Volume / 15f : 0f, Duty: ch.Duty & 3, ShortNoise: i == 3 && ch.NoiseShortMode, Pan: pan);
            }
            return n;
        }

        public int ReadSamples(short[] buffer)
        {
            var scratch = new short[4096]; while (front.ReadSamples(scratch) > 0) { }
            if (driveOnly) return 0;   // the host NES plays and drains its own APU
            foreach (var x in back.GetAudioSamples()) queue.Enqueue(x);
            while (queue.Count > 96000) queue.Dequeue();
            int n = 0;
            while (n + 1 < buffer.Length && queue.Count > 0)
            {
                short v = (short)Math.Clamp(queue.Dequeue() * 32767f, -32768, 32767);
                buffer[n++] = v; buffer[n++] = v;
            }
            return n;
        }
    }
}

namespace NesEmulator
{
    /// <summary>
    /// MIX LAB: a NES APU whose sound comes from a Game Boy sound chip (<see cref="MixConfig.GbApu"/>, any IGbApu).
    /// A NES APU core (MixConfig.NesFrontApu) stays in front for $4015 reads, the frame IRQ and DMC DMA; its own sound
    /// is discarded. The NES channels are rebuilt from the register writes and replayed on the Game Boy channels:
    /// pulses -> pulses (same four duty cycles; NES decay envelopes become Game Boy envelopes), triangle -> the wave
    /// channel playing a 32-step triangle from wave RAM (the NES triangle IS a 32-step, 4-bit waveform, so this one is
    /// exact), noise -> noise (nearest clock, short mode -> 7-bit mode). DMC samples have nowhere to go and are dropped.
    /// Discovered by CoreRegistry as APU id "DMG".
    /// </summary>
    public class APU_DMG : IAPU
    {
        private readonly IAPU front;
        private readonly IGbApu gb;
        /// <summary>Bank 0: the NES chip's own channels replayed on the Game Boy chip's own four.</summary>
        private readonly Lane lane0;
        /// <summary>Banks 1.. (APU_DMGS): more sets of NES channels, each replayed on one of APU_GBS's extra banks. <see cref="extLaneOf"/> by bank (index k - 1 is bank k, null until the bank is
        /// first written), <see cref="extLanes"/> the ones made, in the order they were made (synced in that order).</summary>
        private readonly Lane?[] extLaneOf = new Lane?[APU_GBS.MaxBanks];
        private readonly Lane[] extLanes = new Lane[APU_GBS.MaxBanks];
        private int extLaneCount, extBankCount;
        private readonly IApuExtChannels? gbExt;
        private readonly Queue<float> queue = new();
        private readonly short[] pull = new short[8192];
        private double gbAcc; private int fsAcc, sinceSync, channelMask = 0x1F;

        public APU_DMG(Bus bus) : this(bus, MixConfig.GbApu, extBank: false) { }

        protected APU_DMG(Bus bus, string gbApuId, bool extBank)
        {
            var t = CoreRegistry.ApuTypes.TryGetValue(MixConfig.NesFrontApu, out var ft) ? ft : throw new ArgumentException($"No NES APU '{MixConfig.NesFrontApu}'");
            front = CoreRegistry.CreateInstance<IAPU>(t, bus) ?? throw new InvalidOperationException("front APU");
            gb = GbCores.CreateApu(gbApuId, GbModel.Dmg);
            lane0 = new Lane((r, v) => gb.WriteRegister(r, v));
            if (extBank)
            {
                gbExt = gb as IApuExtChannels ?? throw new NotSupportedException($"Game Boy APU '{gbApuId}' has no extra channel bank");
                AddBanks(1);
                ExtLane(1);      // the second bank is there from the start, as it always was
            }
        }

        /// <summary>Offers the Game Boy chip's extra banks up to <paramref name="banks"/> (the chip grows to them); a bank's lane is made when the bank is first written.</summary>
        private protected void AddBanks(int banks)
        {
            if (gbExt == null) return;
            extBankCount = Math.Max(extBankCount, Math.Min(gbExt.EnsureExtBanks(banks), extLaneOf.Length));
        }

        /// <summary>The extra banks on offer.</summary>
        private protected int ExtBankCount => extBankCount;
        /// <summary>The extra banks written so far (each one a Game Boy bank of four the chip runs).</summary>
        private protected int ExtLaneCount => extLaneCount;

        public virtual string CoreName => $"DMG:{gb.CoreName}+{front.CoreName}";
        public virtual string Description => "MIX LAB: NES channels played on a Game Boy sound chip";
        public int Performance => 0; public int Rating => 1; public string Category => "Experimental";

        /// <summary>Extra bank <paramref name="bank"/> (1..), made the first time it is asked for, for the subclass that exposes them.</summary>
        private protected Lane ExtLane(int bank)
        {
            if (extLaneOf[bank - 1] is { } lane) return lane;
            var ext = gbExt!;
            lane = new Lane((r, v) => ext.WriteExtRegister(bank, (ushort)r, v));
            extLaneOf[bank - 1] = lane; extLanes[extLaneCount++] = lane;
            return lane;
        }

        /// <summary>
        /// One set of NES channels (a <see cref="NesChannelModel"/> fed by register writes) replayed on one bank of Game
        /// Boy sound registers: pulse 1/2, wave (a 32-step triangle from wave RAM) and noise.
        /// </summary>
        private protected sealed class Lane
        {
            public readonly NesChannelModel Model = new();
            private readonly Action<int, byte> write;
            private readonly int[] cache = new int[0x40];
            private readonly bool[] noteOn = new bool[4];
            private readonly int[] lastVol = { -1, -1, -1, -1 }, lastDuty = { -1, -1, -1, -1 };
            private bool triOn;

            public Lane(Action<int, byte> write)
            {
                this.write = write;
                Array.Fill(cache, -1);
                G(0x26, 0x80); G(0x24, 0x77); G(0x25, 0xFF); G(0x10, 0x00);
                // Wave RAM = the NES triangle sequence 15..0, 0..15.
                byte[] tri = { 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
                for (int i = 0; i < 16; i++) write(0x30 + i, tri[i]);
            }

            /// <summary>A NES register write for this bank: the channel model, and the key-on strobes.</summary>
            public void Write(ushort address, byte value)
            {
                Model.Write(address, value);
                if (address == 0x4003) noteOn[0] = true;
                else if (address == 0x4007) noteOn[1] = true;
                else if (address == 0x400F) noteOn[3] = true;
            }

            /// <summary>Write a Game Boy sound register (cached, except trigger writes which are strobes).</summary>
            private void G(int reg, byte v, bool force = false)
            {
                if (!force && cache[reg - 0x10] == v) return;
                cache[reg - 0x10] = v; write(reg, v);
            }

            public void Sync(int channelMask)
            {
                var model = Model;
                for (int i = 0; i < 2; i++)
                {
                    var p = model.P[i]; int b = 0x10 + i * 5;   // NR10/NR11... and NR21 (NR20 does not exist, so pulse 2 starts at $16)
                    if (i == 1) b = 0x15;
                    bool on = p.Audible(i == 0) && (channelMask & (1 << i)) != 0 && !PitchGuard.Blocks(1789773.0 / (16 * (p.Timer + 1)));
                    int x = GbPulsePeriod(p.Timer);
                    if (!on) { if (lastVol[i] != 0) { G(b + 2, 0x00); lastVol[i] = 0; } noteOn[i] = false; continue; }
                    int vol = p.Volume;
                    bool retrigger = noteOn[i] || lastVol[i] <= 0 || lastDuty[i] != p.Duty || (p.Const && vol != lastVol[i]);
                    G(b + 1, (byte)(p.Duty << 6));
                    G(b + 3, (byte)x);
                    if (retrigger)
                    {
                        // Constant volume: fixed envelope. Decay: start at 15 and step down like the NES envelope.
                        byte env = p.Const ? (byte)(vol << 4 | 0x08) : (byte)(0xF0 | Pace(p.Vol));
                        G(b + 2, env, force: true);
                        G(b + 4, (byte)(0x80 | (x >> 8) & 7), force: true);
                        cache[b + 4 - 0x10] = (x >> 8) & 7;
                        lastVol[i] = vol; lastDuty[i] = p.Duty; noteOn[i] = false;
                    }
                    else G(b + 4, (byte)((x >> 8) & 7));
                }
                {
                    double hz = 1789773.0 / (32 * (model.TriTimer + 1));
                    bool on = model.TriAudible && (channelMask & 4) != 0 && !PitchGuard.Blocks(hz);
                    int x = Math.Clamp((int)Math.Round(2048 - 65536 / hz), 0, 2047);
                    if (!on) { if (triOn) { G(0x1A, 0x00); triOn = false; } }
                    else
                    {
                        G(0x1D, (byte)x);
                        if (!triOn) { G(0x1A, 0x80); G(0x1C, 0x20); G(0x1E, (byte)(0x80 | (x >> 8) & 7), force: true); cache[0x1E - 0x10] = (x >> 8) & 7; triOn = true; }
                        else G(0x1E, (byte)((x >> 8) & 7));
                    }
                }
                {
                    bool on = model.NoiseAudible && (channelMask & 8) != 0;
                    if (!on) { if (lastVol[3] != 0) { G(0x21, 0x00); lastVol[3] = 0; } noteOn[3] = false; }
                    else
                    {
                        double f = 1789773.0 / NesChannelModel.NoisePeriods[model.NoisePeriodIdx];
                        int best = 0; double bd = double.MaxValue;
                        for (int s = 0; s < 14; s++) for (int r = 0; r < 8; r++)
                        {
                            double gf = 262144.0 / ((r == 0 ? 0.5 : r) * (1 << s));
                            double d = Math.Abs(Math.Log(gf / f)); if (d < bd) { bd = d; best = s << 4 | r; }
                        }
                        G(0x22, (byte)(best | (model.NoiseShort ? 0x08 : 0)));
                        int vol = model.NoiseVolume;
                        if (noteOn[3] || lastVol[3] <= 0 || (model.NoiseConst && vol != lastVol[3]))
                        {
                            G(0x21, model.NoiseConst ? (byte)(vol << 4 | 0x08) : (byte)(0xF0 | Pace(model.NoiseVol)), force: true);
                            G(0x23, 0x80, force: true);
                            lastVol[3] = vol; noteOn[3] = false;
                        }
                    }
                }
            }
        }

        private static int GbPulsePeriod(int nesTimer) { double hz = 1789773.0 / (16 * (nesTimer + 1)); return Math.Clamp((int)Math.Round(2048 - 131072 / hz), 0, 2047); }

        /// <summary>NES decay envelope period (quarter frames) -> Game Boy envelope pace (1/64 s units).</summary>
        private static int Pace(int nesPeriod) => Math.Clamp((int)Math.Round(64.0 * (nesPeriod + 1) / 240.0), 1, 7);

        public void Step(int cpuCycles)
        {
            front.Step(cpuCycles);
            lane0.Model.Clock(cpuCycles);
            for (int i = 0; i < extLaneCount; i++) extLanes[i].Model.Clock(cpuCycles);
            gbAcc += cpuCycles * (4194304.0 / 1789773.0);
            int t = (int)gbAcc;
            if (t > 0)
            {
                gbAcc -= t; gb.Tick(t);
                for (fsAcc += t; fsAcc >= 8192; fsAcc -= 8192) gb.FrameSequencerStep();
            }
            if ((sinceSync += cpuCycles) >= 1864) { sinceSync = 0; lane0.Sync(channelMask); for (int i = 0; i < extLaneCount; i++) extLanes[i].Sync(channelMask); }
            if (front.GetQueuedSampleCount() > 8192) front.GetAudioSamples();
        }

        public void WriteAPURegister(ushort address, byte value)
        {
            lane0.Write(address, value);
            front.WriteAPURegister(address, value);
        }
        public byte ReadAPURegister(ushort address) => front.ReadAPURegister(address);

        private void Drain()
        {
            int n;
            while ((n = gb.ReadSamples(pull)) > 0)
                for (int i = 0; i + 1 < n; i += 2) queue.Enqueue((pull[i] + pull[i + 1]) / 65536f);
            while (queue.Count > 96000) queue.Dequeue();
        }

        public float[] GetAudioSamples(int maxSamples = 0)
        {
            Drain();
            int n = maxSamples > 0 ? Math.Min(maxSamples, queue.Count) : queue.Count;
            var r = new float[n]; for (int i = 0; i < n; i++) r[i] = queue.Dequeue();
            return r;
        }
        public int GetQueuedSampleCount() { Drain(); return queue.Count; }
        public int GetSampleRate() => gb.SampleRate;
        public void SetEnabledChannels(int channelMask) { this.channelMask = channelMask; front.SetEnabledChannels(channelMask); }
        public object GetState() => front.GetState();
        public void SetState(object state) => front.SetState(state);
        public void ClearAudioBuffers() { Drain(); queue.Clear(); front.ClearAudioBuffers(); }
        public void Reset() { front.Reset(); }
    }

    /// <summary>
    /// The Game Boy sound chip, ENHANCED, on the NES board: <see cref="APU_DMG"/> on the enhanced Game Boy chip <see cref="NesEmulator.Gb.APU_GBS"/>: the NES
    /// chip's own channels on the Game Boy's first four as before, and more banks of NES channels (written through <see cref="IApuExtChannels"/>, like
    /// <see cref="APU_FIXS"/>'s), each on one of the Game Boy chip's extra banks of four - as many as the other console's voices need (two banks when it is
    /// made: 4 pulses, 2 waves, 2 noises). For the cross-console bridges (sound "NES:DMGS"): a SNES or Sega game's voices reach the Game Boy channels all at
    /// once instead of four. Discovered by CoreRegistry as APU id "DMGS".
    /// </summary>
    public sealed class APU_DMGS : APU_DMG, IApuExtChannels
    {
        public APU_DMGS(Bus bus) : base(bus, "GBS", extBank: true) { }

        public override string Description => "MIX LAB: banks of NES channels (as many as the other console's voices need) played on the enhanced Game Boy sound chip";
        public int ExtBanks => ExtBankCount;
        public int MaxExtBanks => APU_GBS.MaxBanks;
        public int EnsureExtBanks(int wanted) { AddBanks(Math.Clamp(wanted, 1, MaxExtBanks)); return ExtBankCount; }
        /// <summary>The extra banks written so far (tests).</summary>
        public int ExtBanksInUse => ExtLaneCount;

        public void WriteExtRegister(int bank, ushort address, byte value)
        {
            if (bank < 1 || bank > ExtBankCount) throw new ArgumentOutOfRangeException(nameof(bank), $"APU_DMGS has extra banks 1 to {ExtBankCount} (EnsureExtBanks adds more, up to {MaxExtBanks})");
            if (address is >= 0x4010 and <= 0x4013) return;   // no DMC: nowhere to play it on the Game Boy
            ExtLane(bank).Write(address, value);
        }
    }
}
