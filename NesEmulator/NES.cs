using System;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Reflection;
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

		// NOTE: In NativeAOT/WebAssembly, System.Text.Json cannot generate serializers at runtime.
		// Using object-typed polymorphic fields (cpu/ppu/apu/mapper) previously forced runtime
		// dynamic IL emission when saving state, triggering a mono "method-builder-ilgen" assertion
		// in flatpublish/native mode. To stay AOT-friendly, we capture each sub-system state as a
		// pre-serialized JSON string instead of a raw object graph. The SetState() methods for
		// subsystems already accept JsonElement, so on load we parse these strings back into
		// JsonDocuments and hand root elements off without requiring polymorphic serialization.
		private class NesState {
			public double cycleRemainder; public byte[] ram = Array.Empty<byte>();
			public string cpu = string.Empty; public string ppu = string.Empty; public string apu = string.Empty; public string mapper = string.Empty; public byte[] prgRAM=Array.Empty<byte>(); public byte[] chrRAM=Array.Empty<byte>();
			public byte controllerState; public byte controllerShift; public bool controllerStrobe; // input
			public byte[] romData = Array.Empty<byte>(); // full iNES ROM image (header+PRG+CHR) for auto-ROM restoration
			public string romHash = string.Empty; // SHA256 of romData for quick comparison
			public bool famicloneMode; // legacy flag for UI backward-compatibility
			public int apuCore; // 0=Modern,1=Jank,2=QuickNes (legacy integer, kept for backward compat if ever needed)
			public int cpuCore; // 0=FMC (future cores enumerate)
			public string cpuCoreId = string.Empty; public string ppuCoreId = string.Empty; public string apuCoreId = string.Empty; // reflection suffixes
		}

		private static string ComputeHash(byte[] data) {
			using var sha = SHA256.Create();
			return string.Concat(sha.ComputeHash(data).Select(b=>b.ToString("x2")));
		}

		// === UI Core Hot-Swap Helpers (CPU / PPU) ===
		public object GetCpuState() => bus?.cpu.GetState() ?? new object();
		public void SetCpuState(object state) { try { bus?.cpu.SetState(state); } catch { } }
		public object GetPpuState() => bus?.ppu.GetState() ?? new object();
		public void SetPpuState(object state) { try { bus?.ppu.SetState(state); } catch { } }
		public void SetCpuCore(Bus.CpuCore core) { try { bus?.SetCpuCore(core); } catch { } }
		public void SetPpuCore(Bus.PpuCore core) { try { bus?.SetPpuCore(core); } catch { } }
		// New generic reflection-based core selection (suffix id strings)
		public bool SetCpuCore(string suffixId) { return bus!=null && bus.SetCpuCoreById(suffixId); }
		public bool SetPpuCore(string suffixId) { return bus!=null && bus.SetPpuCoreById(suffixId); }
		public bool SetApuCore(string suffixId) { return bus!=null && bus.SetApuCoreById(suffixId); }
		public System.Collections.Generic.IReadOnlyList<string> GetCpuCoreIds() => bus?.GetCpuCoreIds() ?? System.Array.Empty<string>();
		public System.Collections.Generic.IReadOnlyList<string> GetPpuCoreIds() => bus?.GetPpuCoreIds() ?? System.Array.Empty<string>();
		public System.Collections.Generic.IReadOnlyList<string> GetApuCoreIds() => bus?.GetApuCoreIds() ?? System.Array.Empty<string>();

		public string SaveState()
		{
			// Verbose diagnostic logging to pinpoint any runtime that might attempt IL generation.
			// This is TEMPORARY instrumentation and may be removed for performance once issue resolved.
			var startTimestamp = DateTime.UtcNow;
			void Log(string msg) { try { Console.WriteLine($"[SaveStateDiag] {{DateTime.UtcNow:O}} {msg}"); } catch {} }
			Log("Begin SaveState() invocation");
			if (bus == null || cartridge == null) { Log("Aborting: bus or cartridge is null"); return string.Empty; }
			#if DEBUG
			var regsDbg = (bus.cpu as ICPU)?.GetRegisters();
			Log($"Initial CPU regs PC={(regsDbg?.PC ?? 0):X4} A={(regsDbg?.A ?? 0):X2} X={(regsDbg?.X ?? 0):X2} Y={(regsDbg?.Y ?? 0):X2} SP={(regsDbg?.SP ?? 0):X4} P={(regsDbg?.P ?? 0):X2}");
			#endif
			string cpuJson = string.Empty, ppuJson = string.Empty, apuJson = string.Empty, mapperJson = string.Empty;
			try { Log("Serializing CPU state - start"); cpuJson = PlainSerialize(bus.cpu.GetState()); Log($"Serializing CPU state - done length={cpuJson.Length}"); } catch (Exception ex) { Log("CPU state serialization FAILED: "+ex.GetType().Name+" "+ex.Message); }
			try { Log("Serializing PPU state - start"); ppuJson = PlainSerialize(bus.ppu.GetState()); Log($"Serializing PPU state - done length={ppuJson.Length}"); } catch (Exception ex) { Log("PPU state serialization FAILED: "+ex.GetType().Name+" "+ex.Message); }
			try { Log("Serializing APU state - start"); apuJson = PlainSerialize(bus.ActiveAPU.GetState()); Log($"Serializing APU state - done length={apuJson.Length}"); } catch (Exception ex) { Log("APU state serialization FAILED: "+ex.GetType().Name+" "+ex.Message); }
			try { Log("Serializing Mapper state - start"); mapperJson = PlainSerialize(cartridge.mapper.GetMapperState()); Log($"Serializing Mapper state - done length={mapperJson.Length}"); } catch (Exception ex) { Log("Mapper state serialization FAILED: "+ex.GetType().Name+" "+ex.Message); }
			NesState? st = null;
			try {
				Log("Cloning raw memory regions");
				var ramClone = (byte[])bus.ram.Clone();
				var prgClone = (byte[])cartridge.prgRAM.Clone();
				var chrClone = (byte[])cartridge.chrRAM.Clone();
				var romClone = (byte[])cartridge.rom.Clone();
				Log($"Sizes ram={ramClone.Length} prgRAM={prgClone.Length} chrRAM={chrClone.Length} rom={romClone.Length}");
				st = new NesState {
					cycleRemainder = cycleRemainder,
					ram = ramClone,
					cpu = cpuJson,
					ppu = ppuJson,
					apu = apuJson,
					mapper = mapperJson,
					prgRAM = prgClone,
					chrRAM = chrClone,
					romData = romClone,
					controllerState = bus.input.DebugGetRawState(),
					controllerShift = bus.input.DebugGetShift(),
					controllerStrobe = bus.input.DebugGetStrobe(),
					famicloneMode = bus.GetFamicloneMode(),
					apuCore = (int)bus.GetActiveApuCore(),
					cpuCore = (int)bus.GetActiveCpuCore(),
					cpuCoreId = bus.cpu.GetType().Name,
					ppuCoreId = bus.ppu.GetType().Name,
					apuCoreId = bus.ActiveAPU.GetType().Name
				};
				Log("NesState object constructed");
			} catch (Exception ex) { Log("NesState construction FAILED: "+ex.GetType().Name+" "+ex.Message); }
			try { if (st != null) { st.romHash = st.romData.Length > 0 ? ComputeHash(st.romData) : string.Empty; Log("ROM hash computed length="+ (st.romHash?.Length ?? 0)); } } catch (Exception ex) { Log("ROM hash FAILED: "+ex.GetType().Name+" "+ex.Message); }
			string json = string.Empty;
			try { Log("Serializing NesState root - start"); json = PlainSerialize(st!); Log($"Serializing NesState root - done length={json.Length}"); } catch (Exception ex) { Log("Root serialization FAILED: "+ex.GetType().Name+" "+ex.Message); }
			var elapsed = DateTime.UtcNow - startTimestamp; Log("SaveState() complete in "+elapsed.TotalMilliseconds.ToString("F2")+" ms");
			return json;
		}

		// Minimal reflection-based serializer supporting primitive fields and primitive arrays.
		// Produces stable JSON without relying on System.Text.Json for AOT-problematic types.
		private static string PlainSerialize(object? obj)
		{
			if (obj == null) return "null";
			if (obj is string s) return '"' + Escape(s) + '"';
			if (obj is byte b) return b.ToString();
			if (obj is sbyte sb) return sb.ToString();
			if (obj is short sh) return sh.ToString();
			if (obj is ushort ush) return ush.ToString();
			if (obj is int i) return i.ToString();
			if (obj is uint ui) return ui.ToString();
			if (obj is long l) return l.ToString();
			if (obj is ulong ul) return ul.ToString();
			if (obj is bool bo) return bo ? "true" : "false";
			if (obj is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (obj is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (obj is byte[] ba) { var sbArr = new StringBuilder(); sbArr.Append('['); for (int k=0;k<ba.Length;k++){ if(k>0) sbArr.Append(','); sbArr.Append(ba[k]); } sbArr.Append(']'); return sbArr.ToString(); }
			if (obj is int[] ia) { var sbArr = new StringBuilder(); sbArr.Append('['); for (int k=0;k<ia.Length;k++){ if(k>0) sbArr.Append(','); sbArr.Append(ia[k]); } sbArr.Append(']'); return sbArr.ToString(); }
			// Generic primitive array support
			if (obj is System.Collections.IEnumerable enumerable && obj.GetType().IsArray)
			{
				var sbArr = new StringBuilder(); sbArr.Append('['); bool first=true; foreach (var el in enumerable) { if(!first) sbArr.Append(','); first=false; sbArr.Append(PlainSerialize(el)); } sbArr.Append(']'); return sbArr.ToString();
			}
			var type = obj.GetType();
			// Limit to class/struct with only primitive/array fields
			// Limit to public instance fields only (AOT: accessing non-public via reflection can trigger dynamic method generation).
			var fields = type.GetFields(BindingFlags.Instance|BindingFlags.Public);
			var sbObj = new StringBuilder(); sbObj.Append('{'); bool firstField=true;
			foreach (var fi in fields)
			{
				if (fi.IsStatic) continue; var val = fi.GetValue(obj);
				if(!firstField) sbObj.Append(','); firstField=false;
				sbObj.Append('"').Append(Escape(fi.Name)).Append('"').Append(':').Append(PlainSerialize(val));
			}
			sbObj.Append('}'); return sbObj.ToString();
			static string Escape(string raw) => raw.Replace("\\","\\\\").Replace("\"","\\\"");
		}

		public void LoadState(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return;
			NesState? st = null;
			try {
				using var doc = System.Text.Json.JsonDocument.Parse(json);
				var root = doc.RootElement;
				st = new NesState();
				if (root.TryGetProperty("cycleRemainder", out var cr)) st.cycleRemainder = cr.GetDouble();
				if (root.TryGetProperty("ram", out var ramEl) && ramEl.ValueKind==System.Text.Json.JsonValueKind.Array){ var arr=ramEl; int len=arr.GetArrayLength(); st.ram=new byte[len]; int idx=0; foreach(var v in arr.EnumerateArray()){ if(idx>=len) break; st.ram[idx++]=(byte)v.GetByte(); } }
				if (root.TryGetProperty("cpu", out var cpuEl) && cpuEl.ValueKind==System.Text.Json.JsonValueKind.String) st.cpu = cpuEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("ppu", out var ppuEl) && ppuEl.ValueKind==System.Text.Json.JsonValueKind.String) st.ppu = ppuEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("apu", out var apuEl) && apuEl.ValueKind==System.Text.Json.JsonValueKind.String) st.apu = apuEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("mapper", out var mapEl) && mapEl.ValueKind==System.Text.Json.JsonValueKind.String) st.mapper = mapEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("prgRAM", out var prgEl) && prgEl.ValueKind==System.Text.Json.JsonValueKind.Array){ int len=prgEl.GetArrayLength(); st.prgRAM=new byte[len]; int i=0; foreach(var v in prgEl.EnumerateArray()){ if(i>=len) break; st.prgRAM[i++]=(byte)v.GetByte(); } }
				if (root.TryGetProperty("chrRAM", out var chrEl) && chrEl.ValueKind==System.Text.Json.JsonValueKind.Array){ int len=chrEl.GetArrayLength(); st.chrRAM=new byte[len]; int i=0; foreach(var v in chrEl.EnumerateArray()){ if(i>=len) break; st.chrRAM[i++]=(byte)v.GetByte(); } }
				if (root.TryGetProperty("controllerState", out var csEl)) st.controllerState = (byte)csEl.GetByte();
				if (root.TryGetProperty("controllerShift", out var cshEl)) st.controllerShift = (byte)cshEl.GetByte();
				if (root.TryGetProperty("controllerStrobe", out var cstEl)) st.controllerStrobe = cstEl.GetBoolean();
				if (root.TryGetProperty("romData", out var romEl) && romEl.ValueKind==System.Text.Json.JsonValueKind.Array){ int len=romEl.GetArrayLength(); st.romData=new byte[len]; int i=0; foreach(var v in romEl.EnumerateArray()){ if(i>=len) break; st.romData[i++]=(byte)v.GetByte(); } }
				if (root.TryGetProperty("romHash", out var rhEl) && rhEl.ValueKind==System.Text.Json.JsonValueKind.String) st.romHash = rhEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("famicloneMode", out var fmEl)) st.famicloneMode = fmEl.GetBoolean();
				if (root.TryGetProperty("apuCore", out var apcEl)) st.apuCore = apcEl.GetInt32();
				if (root.TryGetProperty("cpuCore", out var cpcEl)) st.cpuCore = cpcEl.GetInt32();
				if (root.TryGetProperty("cpuCoreId", out var ccidEl) && ccidEl.ValueKind==System.Text.Json.JsonValueKind.String) st.cpuCoreId = ccidEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("ppuCoreId", out var ppidEl) && ppidEl.ValueKind==System.Text.Json.JsonValueKind.String) st.ppuCoreId = ppidEl.GetString() ?? string.Empty;
				if (root.TryGetProperty("apuCoreId", out var apidEl) && apidEl.ValueKind==System.Text.Json.JsonValueKind.String) st.apuCoreId = apidEl.GetString() ?? string.Empty;
			} catch { st=null; }
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
			if (!string.IsNullOrEmpty(st.mapper)) { try { using var md = System.Text.Json.JsonDocument.Parse(st.mapper); cartridge.mapper.SetMapperState(md.RootElement); } catch { } }
			if (st.prgRAM.Length == cartridge.prgRAM.Length) Array.Copy(st.prgRAM, cartridge.prgRAM, st.prgRAM.Length);
			if (st.chrRAM.Length == cartridge.chrRAM.Length) Array.Copy(st.chrRAM, cartridge.chrRAM, st.chrRAM.Length);
			// Finally restore CPU/PPU/APU internal state
			// Restore CPU core selection (prefer reflection id if present)
			try {
				if (!string.IsNullOrEmpty(st.cpuCoreId))
				{
					var suffix = CoreRegistry.ExtractSuffix(st.cpuCoreId, "CPU_");
					if(!bus.SetCpuCoreById(suffix)) bus.SetCpuCore((Bus.CpuCore)st.cpuCore);
				}
				else bus.SetCpuCore((Bus.CpuCore)st.cpuCore);
			} catch { }
			try { if (!string.IsNullOrEmpty(st.cpu)) { using var cd = System.Text.Json.JsonDocument.Parse(st.cpu); bus.cpu.SetState(cd.RootElement); } } catch { }
			// Restore PPU core selection (prefer reflection id if present)
			try {
				if (!string.IsNullOrEmpty(st.ppuCoreId)) {
					var suffix = CoreRegistry.ExtractSuffix(st.ppuCoreId, "PPU_");
					if(!bus.SetPpuCoreById(suffix)) bus.SetPpuCore(Bus.PpuCore.CUBE);
				} else {
					// prefer FMC if present (user base preference), else fallback priority
					var preferred = new[]{"FMC","CUBE","NGTV","BFR","FIX"};
					bool set=false; foreach(var id in preferred){ if(bus.SetPpuCoreById(id)){ set=true; break; } }
					if(!set) bus.SetPpuCore(Bus.PpuCore.FMC);
				}
			} catch { }
			try { if (!string.IsNullOrEmpty(st.ppu)) { using var pd = System.Text.Json.JsonDocument.Parse(st.ppu); bus.ppu.SetState(pd.RootElement); } } catch { }
			// Restore APU selection before applying APU-specific state (prefer reflection id)
			try {
				if (!string.IsNullOrEmpty(st.apuCoreId))
				{
					var suffix = CoreRegistry.ExtractSuffix(st.apuCoreId, "APU_");
					if(!bus.SetApuCoreById(suffix)) {
						var core = (Bus.ApuCore)st.apuCore; bus.SetApuCore(core);
					}
				}
				else {
					var core = (Bus.ApuCore)st.apuCore; bus.SetApuCore(core);
				}
			} catch { bus.SetFamicloneMode(st.famicloneMode); }
			try { if (!string.IsNullOrEmpty(st.apu)) { using var ad = System.Text.Json.JsonDocument.Parse(st.apu); bus.ActiveAPU.SetState(ad.RootElement); } } catch { }
			// Restore controller
			bus.input.DebugSetState(st.controllerState, st.controllerShift, st.controllerStrobe);
			#if DEBUG
			var regsDbg2 = (bus.cpu as ICPU)?.GetRegisters();
			Console.WriteLine($"[LoadState] Restored PC={(regsDbg2?.PC ?? 0):X4} A={(regsDbg2?.A ?? 0):X2} X={(regsDbg2?.X ?? 0):X2} Y={(regsDbg2?.Y ?? 0):X2} SP={(regsDbg2?.SP ?? 0):X4} status={(regsDbg2?.P ?? 0):X2}");
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
			} catch (CPU_FMC.CpuCrashException ex) {
				if (crashBehavior == CrashBehavior.IgnoreErrors) {
					// Treat crash as recovered; attempt to continue next frame.
					// We could auto-advance PC one byte to avoid infinite loop on same bad opcode.
					// Advance PC by mutating CPU state (hot fix for interface abstraction)
					try { bus.cpu.AddToPC(1); } catch {}
					return; // frame ends early but emulator keeps running
				}
				crashed = true;
				var regsCrash = bus.cpu.GetRegisters();
				crashInfo = ex.Message + " PC=" + regsCrash.PC.ToString("X4");
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

		// --- APU core selection (Modern/Jank/QN) ---
		public enum ApuCore { Modern=0, Jank=1, QuickNes=2 }
		public void SetApuCore(ApuCore core) { if (bus==null) return; bus.SetApuCore((Bus.ApuCore)core); }
		public ApuCore GetApuCore() { if (bus==null) return ApuCore.Jank; return (ApuCore)bus.GetActiveApuCore(); }

		// Debug helper: quick snapshot of CPU registers
		public (ushort PC, byte A, byte X, byte Y, byte P, ushort SP) GetCpuRegs()
		{
			if (bus?.cpu == null) return (0,0,0,0,0,0);
			var r = bus.cpu.GetRegisters();
			return (r.PC, r.A, r.X, r.Y, r.P, r.SP);
		}

		// New helpers: expose active core identifiers for UI (suffixes FMC/FIX etc.)
		public string GetCpuCoreId() => bus?.cpu?.GetType().Name ?? string.Empty;
		public string GetPpuCoreId() => bus?.ppu?.GetType().Name ?? string.Empty;
		public string GetApuCoreId() => bus?.ActiveAPU?.GetType().Name ?? string.Empty;

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
