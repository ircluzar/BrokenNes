// [cer64plus] A running N64 game behind the shape BrokenNes drives consoles through (its IConsoleSession):
// frames in, a 0xAARRGGBB picture, stereo 16-bit sound, a battery save, save states, cores chosen per slot.
// No BrokenNes types are referenced here; a thin wrapper on the BrokenNes side maps PadButtons/ConsoleKind/CoreSlot.
//
// Cores (see Cores/N64Cores.cs): CPU = main emulation (COR64), PPU = video plugin (MADCAT software RDP, PRDP
// parallel-rdp), APU = audio plugin (AI). The session composes them by id, like BrokenNes's SnesSession.
//
// Threading: everything runs on the caller's thread inside RunFrame (the deterministic mode). All emulation state is
// per session, so several sessions can live in one process (each driven by one thread at a time). Telemetry and the
// opcode statistics are process-wide diagnostics only.
using System;
using System.IO;
using cor64.Cores;
using cor64.Mips.R4300I;
using cor64.PIF;

namespace cor64.Session
{
    public enum N64Renderer { Auto, Software, Parallel }

    public sealed class N64SessionOptions
    {
        /// <summary>PPU core id (N64Cores.PpuSoftware / PpuParallel); null picks parallel-rdp when available.</summary>
        public string Ppu { get; set; }
        /// <summary>APU core id; null = N64Cores.ApuAi.</summary>
        public string Apu { get; set; }
        /// <summary>Older switch, used when Ppu is null.</summary>
        public N64Renderer Renderer { get; set; } = N64Renderer.Auto;
        /// <summary>CPU instructions per slice between RCP updates (same meaning as COR64_BATCH); 0 = per-game table
        /// (N64Session.BatchFor: 16 unless the game is listed).</summary>
        public int Batch { get; set; } = 0;
    }

    /// <summary>One controller: buttons in the N64 status-word layout (A=0x8000 ... C-Right=0x0001) and the analog stick.</summary>
    public struct N64Pad
    {
        public JoyController.ButtonPress Buttons;
        public sbyte StickX, StickY;
        public N64Pad(JoyController.ButtonPress buttons, sbyte x = 0, sbyte y = 0) { Buttons = buttons; StickX = x; StickY = y; }
    }

    public sealed class N64Session : IDisposable
    {
        private readonly N64SessionOptions m_Options;
        private readonly Cartridge m_Cart;
        private readonly N64System m_System;
        private readonly byte[] m_PowerOnState;
        private uint[] m_Frame = new uint[320 * 240];

        public N64Session(byte[] rom, N64SessionOptions options = null)
        {
            m_Options = options ?? new N64SessionOptions();
            GameId = Sha1Hex(rom);
            m_Cart = new Cartridge(new MemoryStream(rom, writable: false));

            var cpu = new Interpreter();
            cpu.SetDebuggingMode(false);
            m_System = new N64System();
            m_System.CPU(cpu);
            m_System.DeviceRcp.SetRdpDevice((cor64.Rdp.DrawProcessor)N64Cores.CreatePpu(ResolvePpuId(m_Options)));
            m_System.DeviceRcp.WindowPtr = IntPtr.Zero;
            m_System.AttachApu(N64Cores.CreateApu(m_Options.Apu ?? N64Cores.ApuAi));
            m_System.Boot(m_Cart);

            // Reset restores this snapshot (a power cycle), keeping the battery save.
            m_Batch = m_Options.Batch > 0 ? m_Options.Batch : BatchFor(cor64.IO.SaveDb.GameCode(rom));
            m_PowerOnState = SaveState();
            UpdateFrame();
        }

        private static string ResolvePpuId(N64SessionOptions o)
        {
            if (o.Ppu != null) {
                if (!N64Cores.IsAvailable(N64Slot.Ppu, o.Ppu)) throw new InvalidOperationException($"N64 PPU core {o.Ppu} is not available (parallel-rdp needs cor64_prdp.dll and a Vulkan GPU)");
                return o.Ppu;
            }
            return o.Renderer switch {
                N64Renderer.Software => N64Cores.PpuSoftware,
                N64Renderer.Parallel => cor64.Rdp.ParallelRdp.IsAvailable(true) ? N64Cores.PpuParallel : N64Cores.PpuSoftware,
                _ => N64Cores.DefaultPpu
            };
        }

        public N64System System => m_System;
        public IN64CpuCore Cpu => m_System;
        public IN64PpuCore Ppu => m_System.Ppu;
        public IN64ApuCore Apu => m_System.Apu;
        public N64Renderer Renderer => Ppu.Id == N64Cores.PpuParallel ? N64Renderer.Parallel : N64Renderer.Software;
        public string Title => (m_Cart.Name ?? "").Trim(' ', '\0');
        public string Description => $"N64: CPU {Cpu.Id} ({Cpu.Description}), PPU {Ppu.Id} ({Ppu.Description}), APU {Apu.Id}";
        public string GameId { get; }
        /// <summary>CIC guessed from the boot code CRC (X101..X106 = 6101..6106 NTSC / 7101.. PAL, Unknown).</summary>
        public string Cic => m_Cart.IPL?.Cic.ToString() ?? "Unknown";
        public double FramesPerSecond => m_Cart.Region == RegionType.PAL ? 50.0 : 60.0;

        // ---- input ----
        public void SetPad(int player, N64Pad pad)
        {
            var joys = m_System.Joys;
            if (player < 0 || player >= joys.Length) return;
            if (joys[player] == null) { if (player == 0) return; joys[player] = new JoyController(player); } // [quirk_fix2] ports 2-4 on demand (multiplayer scripts)
            joys[player].SetScript(pad.Buttons, pad.StickX, pad.StickY);
        }

        // ---- running ----
        /// <summary>Runs until the next vertical interrupt (one field), then captures the picture.</summary>
        // measurement only (changes timing): COR64_SESSION_BATCH=n
        private static readonly int s_BatchOverride = int.TryParse(Environment.GetEnvironmentVariable("COR64_SESSION_BATCH"), out var sb) ? sb : 0;
        // [climb4 ge_batch] CPU instructions per slice, per game (keyed like COR64_RSP_SCALE by the two middle letters of
        // the game code). Larger slices change CPU/RCP interleave timing, so only listed games get them; SM64 keeps 16
        // and its exact timing. GoldenEye averages ~5 instructions per block, so every 16-step slice paid a managed entry.
        // [bt_speed] Banjo-Tooie: 512-step slices, 12.6 -> ~8 ms/frame low-end (600-frame hashes unchanged; cert rerun).
        // COR64_BT_BATCH=0 keeps 16.
        // [climb4 speed_c1] Conker (FU) and Resident Evil 2 (RE): 512-step slices. Low-end conker ~16.6 -> ~13.5 ms,
        // re2 ~17.5 -> ~9.4 ms. Conker's 600-frame hashes are unchanged; re2's audio hash changes (interleave).
        // COR64_SPEEDC1_BATCH=0 keeps 16.
        // [climb4 f5_speed2] Factor 5 games (never idle, ~6 instructions per block): 256-step slices.
        // indy 35 -> 23 ms, naboo 32 -> 22.5 ms, rogue 16.3 -> 11.7 ms low-end. Changes their CPU/RCP interleave
        // (output hashes differ from 16). COR64_F5SPEED2_BATCH=0 keeps 16 for them.
        // [climb4 speed_c2] Dr. Mario 64 (N6): 512-step slices. It never idles; its main loop is the 42-step poll cycle the
        // COR64_IDLE_POLL skip handles, which records and matches once per batch, so larger batches skip far more.
        // Low-end 18.3 -> ~3.5 ms/frame (loaded machine). COR64_SPEEDC2_BATCH=0 keeps 16.
        // [climb4 speed_c2] Indy (IJ) and Naboo (NA): 1024-step slices (was 256). Interleaved 4-round medians on a loaded
        // machine: indy 19.03 -> 17.65 ms (0.93), naboo 18.58 -> 16.95 ms (0.91). COR64_SPEEDC2_F5=0 keeps 256.
        // [climb4 speed_cat1] slow library games that never idle (CPU runs the full 1.56M steps per field): larger
        // slices. Changes their CPU/RCP interleave. COR64_SPDCAT1_BATCH=0 keeps 16. Numbers in FINDINGS.md.
        // [climb4 speed_cat2] more slow library games that never idle (catalog_v2 slow list): 1024-step slices. Changes
        // their CPU/RCP interleave. COR64_SPDCAT2_BATCH=0 keeps 16. Numbers in findings/FINDINGS.md.
        // [ingame_fix3] WWF No Mercy (W4) and Doom 64 (DM) never idle: 1024-step slices. Low-end-free bench (warm-up 1500,
        // 300 frames, VI_SPEEDUP=1): No Mercy 26.7 -> 12.1 ms/frame (63 -> 138%), Doom 64 48.7 -> 30.9 ms (34 -> 54%).
        // Changes their CPU/RCP interleave. COR64_INGFIX3_BATCH=0 keeps 16.
        // [speed_poll1] Doom 64 (DM): 1024-step slices with the COR64_SPDPOLL1 poll skip (its osYieldThread loop):
        // 31 -> 117% (bench boot, warm-up 1500). Changes its CPU/RCP interleave. COR64_SPDPOLL1_BATCH=0 keeps 16.
        // [speed_poll2] COR64_SPDPOLL2_BATCH (default on; =0 off): slice sizes for games that get the poll skip in speed_poll2.
        // COR64_SPDPOLL2_FORCEBATCH=<n>: every cart uses n-step slices (measurement only).
        private static readonly int s_Spd2Force = int.TryParse(Environment.GetEnvironmentVariable("COR64_SPDPOLL2_FORCEBATCH"), out var fb) ? fb : 0;
        // [climb4 speed_cat3] default slice for every non-certified game: COR64_SPDCAT3_DEFSLICE=n (default 1024; 0 keeps 16). The 30
        // certified games keep their own slices (SM64 stays bit-exact); COR64_SPDCAT3_EXCL adds comma-separated codes
        // (middle two letters) to the built-in exclusion list of games that got worse. Numbers in findings/FINDINGS.md.
        private static readonly int s_Cat3Def = int.TryParse(Environment.GetEnvironmentVariable("COR64_SPDCAT3_DEFSLICE"), out var c3) ? c3 : 1024;
        // Per-game slice rows (and their COR64_* gates) live in Session/GameDb.cs.
        private static readonly System.Collections.Generic.HashSet<string> s_Cat3ExclEnv = new(
            (Environment.GetEnvironmentVariable("COR64_SPDCAT3_EXCL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        public static int BatchFor(string gameCode)
        {
            if (s_Spd2Force > 0) return s_Spd2Force;
            if (gameCode == null || gameCode.Length < 3) return 16;
            var mid = gameCode.Substring(1, 2);
            if (GameDb.TryGet(GameDb.Setting.Slice, mid, out var k)) return k;
            if (s_Cat3Def > 0 && !GameDb.Has(GameDb.Setting.SliceCert, mid) && !GameDb.Has(GameDb.Setting.SliceExcl, mid) && !s_Cat3ExclEnv.Contains(mid)) return s_Cat3Def;
            return 16;
        }
        private readonly int m_Batch;
        public void RunFrame()
        {
            long vi = Cpu.ViCount;
            long budget = m_System.Ticks + 50_000_000; // guard: a game that never enables the VI still returns
            int batch = Math.Max(1, s_BatchOverride > 0 ? s_BatchOverride : m_Batch);
            while (Cpu.ViCount == vi && m_System.Ticks < budget) {
                Cpu.RunBatch(batch);
                if (cor64.Debug.DebugHooks.Active && cor64.Debug.DebugHooks.Paused) break; // [dbg_exec] stopped mid-frame
            }
            UpdateFrame();
        }

        // [quirk_fix1] the reset button raises the pre-NMI interrupt (Cause IP4) and the game runs 0.5 s with it (PIF
        // timing; mupen64plus behaviour, ares has no pre-NMI) before the reboot below. [prenmi_on] default on;
        // COR64_RESET_PRENMI=0 turns it off (straight power cycle, as before).
        private static readonly bool s_PreNmi = Environment.GetEnvironmentVariable("COR64_RESET_PRENMI") != "0";
        // [prenmi_on] COR64_PRENMI_ON_WARM (default on, only with the pre-NMI): the reboot is a warm NMI reset as on
        // hardware (ares System::power(reset) keeps RDRAM; the PIF reports reset type 1): the CPU restarts from the
        // power-on boot state (IPL3 in DMEM, PC 0xA4000040) with s5 = osResetType = 1 and RDRAM kept, so IPL3 skips the
        // RDRAM init, stores osResetType 1 at 0x8000030C and games keep their osAppNMIBuffer. =0: cold power cycle.
        private static readonly bool s_Warm = Environment.GetEnvironmentVariable("COR64_PRENMI_ON_WARM") != "0";
        /// <summary>Raise the pre-NMI interrupt only (scripts: watch the game's reaction before a reset).</summary>
        public void PreNmi() => m_System.DeviceCPU.Cop0.SetPreNmi();

        public void Reset()
        {
            byte[] ram = null;
            if (s_PreNmi) {
                PreNmi();
                int frames = (int)Math.Round(FramesPerSecond / 2);
                for (int i = 0; i < frames; i++) RunFrame();
                if (s_Warm) ram = ExportRdram();
            }
            var battery = ExportSave();
            LoadState(m_PowerOnState);
            if (ram == null) cor64.Mips.R4300I.SystemController.Boot1Arm(); // [boot_fix1] cold power cycle boots like power-on
            ImportSave(battery);
            if (ram != null) {
                global::System.Runtime.InteropServices.Marshal.Copy(ram, 0, m_System.SystemMemory.RDRAM.GetRamPointer(0), ram.Length);
                m_System.DeviceCPU.State.SetGpr64(21, 1); // s5 = osResetType: NMI
            }
        }

        private byte[] ExportRdram()
        {
            m_System.DeviceRcp.DeviceRdp?.Drain(); // RDP writes in flight land first
            var ram = new byte[8 << 20];
            global::System.Runtime.InteropServices.Marshal.Copy(m_System.SystemMemory.RDRAM.GetRamPointer(0), ram, 0, ram.Length);
            return ram;
        }

        // ---- video ----
        public uint[] Frame => m_Frame;
        public int FrameWidth { get; private set; } = 320;
        public int FrameHeight { get; private set; } = 240;
        /// <summary>N64 output is 4:3 whatever the framebuffer width.</summary>
        public int DisplayWidth => FrameHeight * 4 / 3;

        private void UpdateFrame()
        {
            Ppu.ScanOut(m_System.VI, ref m_Frame, out int w, out int h);
            FrameWidth = w; FrameHeight = h;
        }

        // ---- audio ----
        public int SampleRate => Apu.SampleRate;
        /// <summary>Stereo interleaved samples produced since the last call; returns the number of shorts written.</summary>
        public int ReadSamples(short[] buffer) => Apu.ReadSamples(buffer);

        // ---- battery ----
        // [cer64plus] The cartridge's save memory as raw bytes, sized by its type: EEPROM 512 (4 Kbit) / 2048 (16 Kbit),
        // SRAM 32768 (or 98304), FlashRAM 131072. A 4 Kbit EEPROM game exports exactly the 512 bytes it always did, and
        // any older 512-byte save imports as before. Controller/Rumble Pak: per-port API below (lane pif_pads).
        public bool HasBattery => true;
        public string SaveType { get { var d = m_System.SystemMemory.Dom2; if (d != null && d.IsDomain2) return d.Type.ToString(); return m_System.PIF.Eeprom16K ? "Eeprom16K" : "Eeprom4K"; } }
        /// <summary>Domain-2 save bytes the game wrote since power-on/import (-1: EEPROM game); diagnostics.</summary>
        public long SaveWrites => m_System.SystemMemory.Dom2 is { IsDomain2: true } d ? d.Writes : -1;
        public byte[] ExportSave()
        {
            var d = m_System.SystemMemory.Dom2;
            return d != null && d.IsDomain2 ? d.Export() : m_System.PIF.ExportEeprom();
        }
        public void ImportSave(byte[] data)
        {
            var d = m_System.SystemMemory.Dom2;
            if (d != null && (d.IsDomain2 || (data != null && (data.Length == 0x8000 || data.Length == 0x18000 || data.Length == 0x20000)))) d.Import(data);
            else m_System.PIF.ImportEeprom(data);
        }
        // [cer64plus] lane pif_pads: per-port devices. port 0..3. Defaults: 4 controllers, no paks (COR64_PIF_PADS / COR64_PIF_PAK).
        public void SetControllerConnected(int port, bool connected) => m_System.PIF.Ports.Connected[port] = connected;
        public void SetPak(int port, PakKind kind) => m_System.PIF.Ports.SetPak(port, kind);
        public PakKind GetPak(int port) => m_System.PIF.Ports.Pak[port];
        public byte[] ExportControllerPak(int port) => m_System.PIF.Ports.ExportPak(port);
        public void ImportControllerPak(int port, byte[] data) => m_System.PIF.Ports.ImportPak(port, data);

        // ---- core swap ----
        /// <summary>
        /// PPU: swapped in place at the frame boundary. CPU, RSP, RDRAM and audio carry on exactly (the machine state goes
        /// through a savestate round trip); the new renderer starts with fresh internal state (tiles, modes, TMEM), which
        /// the game sets again for every draw, so the picture is right again within a frame.
        /// APU: swapped in place (the AI pushes into the new core). CPU: one core, refused.
        /// </summary>
        public bool TrySwapCore(N64Slot slot, string id)
        {
            if (!N64Cores.IsAvailable(slot, id)) return false;
            switch (slot) {
                case N64Slot.Ppu:
                    if (string.Equals(Ppu.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
                    // machine state only: the audio core keeps its pending samples, because the game does not stop
                    using (var ms = new MemoryStream(16 << 20)) {
                        cor64.SaveState.Save(m_System, ms);
                        m_System.DeviceRcp.SwapRdpDevice((cor64.Rdp.DrawProcessor)N64Cores.CreatePpu(id));
                        ms.Position = 0;
                        cor64.SaveState.Load(m_System, ms);
                    }
                    UpdateFrame();
                    return true;
                case N64Slot.Apu:
                    if (string.Equals(Apu.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
                    Apu.FlushPending();
                    m_System.AttachApu(N64Cores.CreateApu(id));
                    return true;
                default:
                    return false;
            }
        }

        public string SwapRefusal(N64Slot slot, string id) =>
            !N64Cores.IsAvailable(slot, id) ? $"the N64 {slot} core {id} is not available on this machine"
            : slot == N64Slot.Cpu ? "the N64 has one CPU core (COR64); there is nothing to swap it with"
            : $"the N64 session cannot change its {slot} core to {id} while the game runs";

        // ---- save states (payload only; the host wraps it in its own container) ----
        public bool SupportsState => true;
        public string StateFormat => "cor64-st2";

        public byte[] SaveState()
        {
            Apu.FlushPending(); // every sample made before the snapshot reaches ReadSamples
            using var ms = new MemoryStream(16 << 20);
            cor64.SaveState.Save(m_System, ms);
            return ms.ToArray();
        }

        /// <summary>Throws InvalidDataException (message fit for the user) when the state is not for this game or is damaged.</summary>
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length < 16) throw new InvalidDataException("The save state is empty or damaged.");
            cor64.SaveState.Load(m_System, new MemoryStream(state, writable: false));
            Apu.DiscardPending(); // ReadSamples now returns only sound made after the load
            UpdateFrame();
        }

        public byte[] SaveStatePayload() => SaveState();

        public void Dispose()
        {
            Ppu?.Dispose();
            (m_System?.DeviceCPU as cor64.Mips.R4300I.Interpreter)?.ReleaseBlocks(); // [mem_fix] unroot the session
        }

        private static string Sha1Hex(byte[] data)
        {
            var h = global::System.Security.Cryptography.SHA1.HashData(data);
            return Convert.ToHexString(h).ToLowerInvariant();
        }
    }
}
