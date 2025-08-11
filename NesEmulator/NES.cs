using System;
using System.Security.Cryptography;
using System.Linq;
namespace NesEmulator
{
	public class NES
	{
		private Cartridge? cartridge;
		private Bus? bus;
		private bool forceStatic = false; // when true, draw animated gray static instead of PPU output
		private double cycleRemainder = 0; // leftover CPU cycles from previous frame
		// Removed frameskip to maintain consistent visual cadence
		private const double CpuFrequency = 1789773.0; // NTSC CPU frequency
		private const double TargetFps = 60.0;
		private const double CyclesPerFrame = CpuFrequency / TargetFps; // ~29829.55

		public NES() { }
		private bool crashed = false; private string crashInfo = string.Empty; private CrashKind crashKind = CrashKind.Generic;
		private enum CrashKind { Generic, UnsupportedMapper }
		public enum CrashBehavior { RedScreen, IgnoreErrors }
		private CrashBehavior crashBehavior = CrashBehavior.RedScreen;
		public void SetCrashBehavior(CrashBehavior behavior) { crashBehavior = behavior; if (behavior == CrashBehavior.IgnoreErrors) { crashed = false; crashInfo = string.Empty; if (bus?.cpu != null) bus.cpu.IgnoreInvalidOpcodes = true; } else if (bus?.cpu != null) { bus.cpu.IgnoreInvalidOpcodes = false; } }

		// --- Visual test helpers ---
		public void EnableStatic(bool on=true) { forceStatic = on; }
		public bool IsStaticEnabled() => forceStatic;

		// --- Public crash state helpers (for UI/debug) ---
		public bool IsCrashed() => crashed;
		public string GetCrashInfo() => crashInfo;
		public void ForceCrash(string reason = "Forced crash") {
			crashed = true; crashInfo = reason; crashKind = CrashKind.Generic; RenderCrashScreen();
		}

		private class NesState {
			public double cycleRemainder; public byte[] ram = Array.Empty<byte>();
			public object cpu = default!; public object ppu = default!; public object apu = default!; public object mapper = default!; public byte[] prgRAM=Array.Empty<byte>(); public byte[] chrRAM=Array.Empty<byte>();
			public byte controllerState; public byte controllerShift; public bool controllerStrobe; // input
			public byte[] romData = Array.Empty<byte>(); // full iNES ROM image (header+PRG+CHR) for auto-ROM restoration
			public string romHash = string.Empty; // SHA256 of romData for quick comparison
			public bool famicloneMode; // active APU core mode
		}

		private static string ComputeHash(byte[] data) {
			using var sha = SHA256.Create();
			return string.Concat(sha.ComputeHash(data).Select(b=>b.ToString("x2")));
		}

		public string SaveState()
		{
			if (bus == null || cartridge == null) return string.Empty;
			#if DEBUG
			Console.WriteLine($"[SaveState] PC={(bus.cpu.PC):X4} A={bus.cpu.A:X2} X={bus.cpu.X:X2} Y={bus.cpu.Y:X2} SP={bus.cpu.SP:X4} status={bus.cpu.status:X2}");
			#endif
			var st = new NesState {
				cycleRemainder = cycleRemainder,
				ram = (byte[])bus.ram.Clone(),
				cpu = bus.cpu.GetState(),
				ppu = bus.ppu.GetState(),
				apu = bus.ActiveAPU.GetState(),
				mapper = cartridge.mapper.GetMapperState(),
				prgRAM = (byte[])cartridge.prgRAM.Clone(),
				chrRAM = (byte[])cartridge.chrRAM.Clone(),
				romData = (byte[])cartridge.rom.Clone(),
				controllerState = bus.input.DebugGetRawState(),
				controllerShift = bus.input.DebugGetShift(),
				controllerStrobe = bus.input.DebugGetStrobe(),
				famicloneMode = bus.GetFamicloneMode()
			};
			st.romHash = st.romData.Length > 0 ? ComputeHash(st.romData) : string.Empty;
			var json = System.Text.Json.JsonSerializer.Serialize(st, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
			#if DEBUG
			Console.WriteLine($"[SaveState] JSON length={json.Length}");
			#endif
			return json;
		}

		public void LoadState(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return;
			var st = System.Text.Json.JsonSerializer.Deserialize<NesState>(json, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
			if (st == null) return;
			// If ROM data was saved and either no cartridge loaded or ROM differs, rebuild cartridge+bus
			bool needNewCartridge = false;
			if (st.romData != null && st.romData.Length >= 16) {
				if (cartridge == null) needNewCartridge = true;
				else {
					// quick compare by length + maybe hash if provided
					if (cartridge.rom.Length != st.romData.Length) needNewCartridge = true;
					else if (!string.IsNullOrEmpty(st.romHash)) {
						var currentHash = ComputeHash(cartridge.rom);
						if (!string.Equals(currentHash, st.romHash, StringComparison.OrdinalIgnoreCase)) needNewCartridge = true;
					}
				}
			}
			if (needNewCartridge) {
				try {
					if (st.romData == null || st.romData.Length == 0) return; // cannot rebuild without ROM bytes
					cartridge = new Cartridge(st.romData);
					bus = new Bus(cartridge);
					bus.cpu.Reset(); // establish baseline then overwrite with state
				} catch { return; }
			}
			if (bus == null || cartridge == null) return; // still cannot proceed
			cycleRemainder = st.cycleRemainder;
			if (st.ram != null && st.ram.Length == bus.ram.Length) Array.Copy(st.ram, bus.ram, st.ram.Length);
			// Restore mapper first so CPU/PPU memory fetches align when we set their internals
			if (st.mapper != null) cartridge.mapper.SetMapperState(st.mapper);
			if (st.prgRAM.Length == cartridge.prgRAM.Length) Array.Copy(st.prgRAM, cartridge.prgRAM, st.prgRAM.Length);
			if (st.chrRAM.Length == cartridge.chrRAM.Length) Array.Copy(st.chrRAM, cartridge.chrRAM, st.chrRAM.Length);
			// Finally restore CPU/PPU/APU internal state
			bus.cpu.SetState(st.cpu);
			bus.ppu.SetState(st.ppu);
			// restore APU mode before applying state
			bus.SetFamicloneMode(st.famicloneMode);
			bus.ActiveAPU.SetState(st.apu);
			// Restore controller
			bus.input.DebugSetState(st.controllerState, st.controllerShift, st.controllerStrobe);
			#if DEBUG
			Console.WriteLine($"[LoadState] Restored PC={(bus.cpu.PC):X4} A={bus.cpu.A:X2} X={bus.cpu.X:X2} Y={bus.cpu.Y:X2} SP={bus.cpu.SP:X4} status={bus.cpu.status:X2}");
			#endif
		}

		public void LoadROM(byte[] romData)
		{
			try {
				// Preserve currently selected famiclone/native APU mode across cartridge swaps
				bool prevFamiClone = bus?.GetFamicloneMode() ?? true;
				cartridge = new Cartridge(romData);
				bus = new Bus(cartridge);
				bus.SetFamicloneMode(prevFamiClone); // restore user preference before clearing cores
				// Ensure fresh audio cores (avoid previous game's APU state bleeding into new one or mode desync)
				bus.HardResetAPUs();
				bus.cpu.Reset();
				// Apply current crash behavior to fresh CPU instance
				bus.cpu.IgnoreInvalidOpcodes = crashBehavior == CrashBehavior.IgnoreErrors;
				crashed = false; crashInfo = string.Empty; crashKind = CrashKind.Generic;
			}
			catch (Cartridge.UnsupportedMapperException ume) {
				// Force crash screen even if ignoring errors
				crashed = true;
				crashInfo = $"Mapper {ume.MapperId} ({ume.MapperName}) is not supported";
				crashKind = CrashKind.UnsupportedMapper;
				RenderCrashScreen();
			}
		}

		public void RunFrame()
		{
			if (bus == null || crashed) return;
			// Determine cycles target for this frame (carry fractional remainder)
			double targetCycles = CyclesPerFrame + cycleRemainder;
			int targetInt = (int)targetCycles;
			int executed = 0;
			// Loop unrolled in small batches to reduce loop condition checks
			try {
				while (executed < targetInt)
				{
					for (int i = 0; i < 8 && executed < targetInt; i++)
					{
						int cpuCycles = bus.cpu.ExecuteInstruction();
						executed += cpuCycles;
						int ppuCycles = cpuCycles * 3;
						bus.ppu.Step(ppuCycles);
							bus.StepAPU(cpuCycles);
					}
				}
			} catch (CPU.CpuCrashException ex) {
				if (crashBehavior == CrashBehavior.IgnoreErrors) {
					// Treat crash as recovered; attempt to continue next frame.
					// We could auto-advance PC one byte to avoid infinite loop on same bad opcode.
					bus.cpu.PC++; // advance past offending opcode
					return; // frame ends early but emulator keeps running
				}
				crashed = true;
				crashInfo = ex.Message + " PC=" + bus.cpu.PC.ToString("X4");
				RenderCrashScreen();
				return;
			}
			cycleRemainder = executed - targetCycles; // may be negative or small positive
			// Always update frame buffer (no frameskip) for smoother perceived motion
			if (!crashed) bus.ppu.UpdateFrameBuffer();
		}

		// Allow caller to rapidly run multiple frames (used for fast-forward / warm-up)
		public void RunFrames(int frames)
		{
			for (int i = 0; i < frames; i++) RunFrame();
		}

		public byte[] GetFrameBuffer()
		{
			if (crashed) return crashFrameBuffer;
			if (forceStatic && bus?.ppu != null) {
				bus.ppu.GenerateStaticFrame();
				return bus.ppu.GetFrameBuffer();
			}
			if (bus?.ppu != null) return bus.ppu.GetFrameBuffer();
			return new byte[256 * 240 * 4];
		}

		private byte[] crashFrameBuffer = new byte[256*240*4];
		private void RenderCrashScreen() {
			for (int i=0;i<256*240;i++) { crashFrameBuffer[i*4+0]=200; crashFrameBuffer[i*4+1]=0; crashFrameBuffer[i*4+2]=0; crashFrameBuffer[i*4+3]=255; }
			if (crashKind == CrashKind.UnsupportedMapper) {
				DrawTextOnCrash("UNSUPPORTED MAPPER", 8, 8);
				DrawTextOnCrash(crashInfo, 8, 20);
				DrawTextOnCrash("Cannot run this ROM", 8, 32);
			} else {
				DrawTextOnCrash("CRASHED", 8, 8);
				DrawTextOnCrash(crashInfo, 8, 20);
				DrawTextOnCrash("Switch to 'Ignore' to continue", 8, 32);
			}
		}
		private void DrawTextOnCrash(string text, int x, int y) {
			// Simple 3x5 font upscale to 6x10 for readability
			int w=256;
			// Bitmap font for ASCII 32..95 (subset) using 5 rows of 3 bits (packed into bytes)
			// We'll synthesize characters procedurally for simplicity (A-Z, 0-9, space, punctuation fallback)
			foreach (char raw in text) {
				char c = char.ToUpperInvariant(raw);
				for (int dy=0; dy<10; dy++) {
					for (int dx=0; dx<6; dx++) {
						bool on = CrashFontPixel(c, dx/2, dy/2); // map 6x10 -> 3x5
						if (!on) continue;
						int px = x+dx; int py=y+dy; if (px>=0 && px<w && py>=0 && py<240) { int idx=(py*w+px)*4; crashFrameBuffer[idx]=255; crashFrameBuffer[idx+1]=255; crashFrameBuffer[idx+2]=255; }
					}
				}
				x += 7; // advance
			}
		}
		private bool CrashFontPixel(char c, int x, int y) {
			// 3x5 pattern; crude definitions
			// Use switch for key chars, default diagonal pattern
			if (x<0||x>2||y<0||y>4) return false;
			switch (c) {
				case 'A': return (y==0&&x>0)||(y>0&&(x==0||x==2))||y==2;
				case 'B': return x==0|| (y==0||y==2||y==4)&&x<2 || (x==2&& y!=0 && y!=2 && y!=4);
				case 'C': return (y==0||y==4)&&x>0 || (x==0 && y>0 && y<4);
				case 'D': return x==0 || (x==2 && y>0 && y<4) || (y==0||y==4)&&x<2;
				case 'E': return x==0 || y==0 || y==2 || y==4;
				case 'F': return x==0 || y==0 || y==2;
				case 'G': return (y==0||y==4)&&x>0 || (x==0&&y>0&&y<4) || (y==2&&x==2) || (x==2&&y>2&&y<4);
				case 'H': return x==0||x==2||y==2;
				case 'I': return y==0||y==4||x==1;
				case 'N': return x==0||x==2|| (x==1&&y==1)|| (x==1&&y==3);
				case 'O': return (y==0||y==4)&&x>0 || (x==0||x==2)&&y>0&&y<4;
				case 'P': return x==0|| (y==0||y==2)&&x<2 || (x==2&&y==1);
				case 'R': return x==0|| (y==0||y==2)&&x<2 || (x==2&&y==1) || (x==1&&y==3) || (x==2&&y==4);
				case 'S': return (y==0||y==2||y==4)&&x>0 || (x==0&&y>0&&y<2) || (x==2&&y>2&&y<4);
				case 'T': return y==0 || x==1;
				case 'U': return (x==0||x==2)&&y<4 || (y==4&&x>0);
				case 'V': return (x==0||x==2)&&y<3 || (x==1&&y>=3);
				case '0': return (y==0||y==4)&&x>0 || (x==0||x==2)&&y>0&&y<4;
				case '1': return x==1 || (y==4&&x!=0);
				case '2': return (y==0||y==2||y==4)&&x>0 || (x==2&&y==1) || (x==0&&y>2&&y<4);
				case '3': return (y==0||y==2||y==4)&&x>0 || (x==2&&y==1) || (x==2&&y==3);
				case '4': return (x==2&&y<3) || (y==2) || (x==0&&y<2);
				case '5': return (y==0||y==2||y==4)&&x<2 || (x==0&&y<2) || (x==2&&y>2&&y<4);
				case '6': return (y==0||y==2||y==4)&&x>0 || (x==0&&y>0&&y<4) || (x==2&&y>2&&y<4);
				case '7': return y==0 || x==2 && y>0;
				case '8': return (y==0||y==2||y==4)&&x>0 || (x==0||x==2)&&y>0&&y<4;
				case '9': return (y==0||y==2)&&x>0 || (x==0&&y>0&&y<2) || (x==2&&y>0&&y<4) || (y==4&&x<2);
				case ' ': return false;
				case '-': return y==2;
				case ':': return (y==1||y==3)&&x==1;
				default: return (x==y) || (x==2-y); // X pattern fallback
			}
		}

		public void SetInput(bool[] buttons)
		{
			bus?.input.SetInput(buttons);
		}

		// Get audio buffer from APU
		public float[] GetAudioBuffer()
		{
			if (bus != null)
				return bus.GetAudioSamples(2048);
			var silentBuffer = new float[2048]; Array.Fill(silentBuffer,0f); return silentBuffer;
		}

		public int GetQueuedAudioSamples() => bus?.GetQueuedSamples() ?? 0;
		public int GetAudioSampleRate() => bus?.GetAudioSampleRate() ?? 44100;

		public bool GetFamicloneMode() => bus?.GetFamicloneMode() ?? true;
		public void ToggleFamicloneMode() { if (bus==null) return; bus.SetFamicloneMode(!bus.GetFamicloneMode()); }
		public void SetFamicloneMode(bool on) { if (bus==null) return; bus.SetFamicloneMode(on); }

		// Debug helper: quick snapshot of CPU registers
		public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetCpuRegs()
		{
			if (bus?.cpu == null) return (0,0,0,0,0,0);
			return (bus.cpu.PC, bus.cpu.A, bus.cpu.X, bus.cpu.Y, bus.cpu.status, bus.cpu.SP);
		}

		// Lightweight RAM digest (sum of first 64 and last 64 bytes) to observe changes without hashing entire array repeatedly
		public string GetStateDigest()
		{
			if (bus == null) return "NO BUS";
			var (pc,a,x,y,p,sp) = GetCpuRegs();
			int sumStart=0,sumEnd=0;
			for (int i=0;i<64 && i<bus.ram.Length;i++) sumStart+=bus.ram[i];
			for (int i=0;i<64 && i<bus.ram.Length;i++) sumEnd+=bus.ram[bus.ram.Length-1-i];
			return $"PC={pc:X4} A={a:X2} X={x:X2} Y={y:X2} P={p:X2} SP={sp:X4} RS={sumStart:X4}/{sumEnd:X4}";
		}

		// === Public memory access helpers (for UI / tooling) ===
		public byte PeekCpu(ushort addr) => bus?.PeekByte(addr) ?? (byte)0;
		public void PokeCpu(ushort addr, byte val) { bus?.PokeByte(addr, val); }
		public byte PeekSystemRam(int index) => bus?.PeekRam(index) ?? (byte)0;
		public void PokeSystemRam(int index, byte val) { bus?.PokeRam(index, val); }
		public byte PeekPrg(int index) => cartridge != null ? cartridge.PeekPrg(index) : (byte)0;
		public void PokePrg(int index, byte val) { cartridge?.PokePrg(index, val); }
		public byte PeekPrgRam(int index) => cartridge != null ? cartridge.PeekPrgRam(index) : (byte)0;
		public void PokePrgRam(int index, byte val) { cartridge?.PokePrgRam(index, val); }
		public byte PeekChr(int index) => cartridge != null ? cartridge.PeekChr(index) : (byte)0;
		public void PokeChr(int index, byte val) { cartridge?.PokeChr(index, val); }

		// Audio system not yet implemented - would require APU (Audio Processing Unit)
		// The APU generates square waves, triangle waves, noise, and DMC audio channels
		// This is a complex subsystem that would handle:
		// - Square wave channels with envelope, sweep, and length counter
		// - Triangle wave channel 
		// - Noise channel for percussion/sound effects
		// - DMC (Delta Modulation Channel) for sample playback
	}
}
