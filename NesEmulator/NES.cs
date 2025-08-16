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
		// --- Fixed-point frame timing (Optimization #4) ---
		// Replaces prior double-based fractional cycle accounting. We model CPU cycles per frame as:
		//   CpuFrequencyInt = BaseCyclesPerFrame * 60 + ExtraCyclesNumerator
		// Each frame: target = BaseCyclesPerFrame plus one extra cycle on ExtraCyclesNumerator of 60 frames
		// (Exact ratio 1789773 / 60 = 29829 + 33/60; simplifies to 29829 + 11/20).
		// We also carry instruction overshoot (cycles executed beyond frame target) to the next frame.
		// Fields are persisted for savestates (see NesState.extraCycleAcc / overshootCarry for new integers).
		private int extraCycleAccumulator = 0; // 0..ExtraCyclesDenominator-1 scaled accumulator
		private int overshootCarry = 0; // cycles executed beyond prior frame's target, subtract from next target
		private double cycleRemainder = 0; // legacy field kept for backward-compat load of old states
		// Removed frameskip to maintain consistent visual cadence
		private const double CpuFrequency = 1789773.0; // NTSC CPU frequency (double kept for benchmarks)
		private const int CpuFrequencyInt = 1789773; // integer form for fixed-point arithmetic
		private const int TargetFpsInt = 60;
		private const int BaseCyclesPerFrame = CpuFrequencyInt / TargetFpsInt; // 29829
		private const int ExtraCyclesNumerator = CpuFrequencyInt % TargetFpsInt; // 33
		private const int ExtraCyclesDenominator = TargetFpsInt; // 60

		public NES() { }
		public string RomName { get; set; } = string.Empty; // optional UI label propagated into savestates
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
			public double cycleRemainder; // legacy (pre fixed-point). Retained for backward compatibility.
			public int extraCycleAcc; // new: accumulator for fractional cycles (0..ExtraCyclesDenominator-1)
			public int overshootCarry; // new: instruction overshoot carry to next frame
			public byte[] ram = Array.Empty<byte>();
			public string cpu = string.Empty; public string ppu = string.Empty; public string apu = string.Empty; public string mapper = string.Empty; public byte[] prgRAM=Array.Empty<byte>(); public byte[] chrRAM=Array.Empty<byte>();
			public byte controllerState; public byte controllerShift; public bool controllerStrobe; // input
			public byte[] romData = Array.Empty<byte>(); // full iNES ROM image (header+PRG+CHR) for auto-ROM restoration
			public string romHash = string.Empty; // SHA256 of romData for quick comparison
			public string romName = string.Empty; // optional metadata for UI labeling
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
			// Verbose diagnostic logging (DEBUG only) - stripped in Release (Optimization #19)
			var startTimestamp = DateTime.UtcNow;
			#if DIAG_LOG
			void Log(string msg) { try { Console.WriteLine($"[SaveStateDiag] {{DateTime.UtcNow:O}} {msg}"); } catch {} }
			#else
			void Log(string msg) { }
			#endif
			Log("Begin SaveState() invocation");
			if (bus == null || cartridge == null) { Log("Aborting: bus or cartridge is null"); return string.Empty; }
			#if DIAG_LOG
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
					cycleRemainder = overshootCarry, // store overshoot in legacy field for older loaders
					extraCycleAcc = extraCycleAccumulator,
					overshootCarry = overshootCarry,
					ram = ramClone,
					cpu = cpuJson,
					ppu = ppuJson,
					apu = apuJson,
					mapper = mapperJson,
					prgRAM = prgClone,
					chrRAM = chrClone,
					romData = romClone,
					romName = RomName,
					controllerState = bus.input.DebugGetRawState(),
					controllerShift = bus.input.DebugGetShift(),
					controllerStrobe = bus.input.DebugGetStrobe(),
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

		public string GetSavedRomName(string stateJson)
		{
			try { using var doc = System.Text.Json.JsonDocument.Parse(stateJson); if (doc.RootElement.TryGetProperty("romName", out var rn) && rn.ValueKind==System.Text.Json.JsonValueKind.String) return rn.GetString()??string.Empty; } catch {} return string.Empty;
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
			var sbObj = new StringBuilder(); sbObj.Append('{'); bool firstMember=true;
			foreach (var fi in fields)
			{
				if (fi.IsStatic) continue; var val = fi.GetValue(obj);
				if(!firstMember) sbObj.Append(','); firstMember=false;
				sbObj.Append('"').Append(Escape(fi.Name)).Append('"').Append(':').Append(PlainSerialize(val));
			}
			// Theory 3 fix: also include eligible public properties (primitive, string, primitive arrays) with both getter and setter
			try {
				var props = type.GetProperties(BindingFlags.Instance|BindingFlags.Public);
				foreach (var pi in props)
				{
					if (!pi.CanRead || !pi.CanWrite) continue; // require read/write to restore later
					var pt = pi.PropertyType;
					bool include = false;
					if (pt.IsPrimitive || pt == typeof(string)) include = true;
					else if (pt.IsArray) {
						var et = pt.GetElementType();
						if (et != null && (et.IsPrimitive || et == typeof(string))) include = true;
					}
					if (!include) continue;
					object? val;
					try { val = pi.GetValue(obj); } catch { continue; }
					if(!firstMember) sbObj.Append(','); firstMember=false;
					sbObj.Append('"').Append(Escape(pi.Name)).Append('"').Append(':').Append(PlainSerialize(val));
				}
			} catch { /* ignore reflection issues; keep serializer robust */ }
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
			// Restore fixed-point timing accumulators (fallback to legacy double if new ints absent)
			if (st.extraCycleAcc != 0 || st.overshootCarry != 0)
			{
				extraCycleAccumulator = st.extraCycleAcc;
				overshootCarry = st.overshootCarry;
			}
			else
			{
				// Legacy state: interpret positive cycleRemainder as overshoot carry
				overshootCarry = st.cycleRemainder > 0 ? (int)st.cycleRemainder : 0;
				extraCycleAccumulator = 0;
			}
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
			try { bus.ppu?.ClearBuffers(); } catch { }
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
			} catch {
				// Back-compat: if no apuCoreId nor apuCore usable, attempt to infer from legacy boolean if present
				try {
					using var doc2 = System.Text.Json.JsonDocument.Parse(json);
					if (doc2.RootElement.TryGetProperty("famicloneMode", out var fmEl))
					{
						bool fm = fmEl.GetBoolean();
						bus.SetApuCore(fm ? Bus.ApuCore.Jank : Bus.ApuCore.Modern);
					}
				} catch { }
			}
			try { if (!string.IsNullOrEmpty(st.apu)) { using var ad = System.Text.Json.JsonDocument.Parse(st.apu); bus.ActiveAPU.SetState(ad.RootElement); } } catch { }
			// Drop any queued audio/pacing so playback restarts cleanly after state load
			try { bus.ActiveAPU?.ClearAudioBuffers(); } catch { }
			// Restore controller
			bus.input.DebugSetState(st.controllerState, st.controllerShift, st.controllerStrobe);
			#if DIAG_LOG
			try {
				var activeCpuId = bus.cpu?.GetType().Name ?? "";
				var activePpuId = bus.ppu?.GetType().Name ?? "";
				var activeApuId = bus.ActiveAPU?.GetType().Name ?? "";
				Console.WriteLine($"[LoadState] SavedCoreIds CPU={st.cpuCoreId} PPU={st.ppuCoreId} APU={st.apuCoreId} | Active CPU={activeCpuId} PPU={activePpuId} APU={activeApuId}");
			} catch {}
			#endif
			#if DIAG_LOG
			var regsDbg2 = (bus.cpu as ICPU)?.GetRegisters();
			Console.WriteLine($"[LoadState] Restored PC={(regsDbg2?.PC ?? 0):X4} A={(regsDbg2?.A ?? 0):X2} X={(regsDbg2?.X ?? 0):X2} Y={(regsDbg2?.Y ?? 0):X2} SP={(regsDbg2?.SP ?? 0):X4} status={(regsDbg2?.P ?? 0):X2}");
			#endif
		}

		public void LoadROM(byte[] romData)
		{
			try {
				// Preserve currently selected APU core across cartridge swaps
				var prevApu = bus?.GetActiveApuCore() ?? Bus.ApuCore.Jank;
				cartridge = new Cartridge(romData);
				bus = new Bus(cartridge);
				if (string.IsNullOrEmpty(RomName)) RomName = "(ROM)"; // fallback label if UI doesn't set
				bus.SetApuCore(prevApu); // restore user preference before clearing cores
				// Ensure fresh audio cores (avoid previous game's APU state bleeding into new one or mode desync)
				bus.HardResetAPUs();
				try { bus.ppu?.ClearBuffers(); } catch { }
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
			framesExecutedTotal++;
			// Compute target cycles for this frame using integer fixed-point method.
			// Base cycles plus an extra cycle on frames where accumulator crosses denominator.
			int targetCycles = BaseCyclesPerFrame;
			extraCycleAccumulator += ExtraCyclesNumerator; // accumulate fractional part (33 per frame)
			if (extraCycleAccumulator >= ExtraCyclesDenominator) { targetCycles++; extraCycleAccumulator -= ExtraCyclesDenominator; }
			// Apply any overshoot carry from last frame (can reduce target this frame)
			if (overshootCarry > 0) {
				if (overshootCarry >= targetCycles) {
					// Edge case: previous overshoot larger than base frame; clamp to leave minimum work
					overshootCarry -= targetCycles;
					return; // skip running CPU this frame; leftover overshoot still pending
				}
				targetCycles -= overshootCarry; overshootCarry = 0;
			}
			int executed = 0; // cycles executed this frame (relative)
			long frameEndCycle = globalCpuCycle + targetCycles; // absolute cycle where this frame ends
			nextFrameBoundaryCycle = frameEndCycle; // update per-frame boundary
			if (EnableEventScheduler)
			{
				// --- Experimental event-driven path (Feature flag gated) ---
				// Ensure first PPU event is scheduled (scanline end) if not already or stale
				if (nextPpuEventCycle <= globalCpuCycle) ScheduleNextPpuScanline();
				if (nextApuEventCycle <= globalCpuCycle) ScheduleNextApuEvent();
				// Disable inline interrupt polling to rely on boundary servicing
				try { (bus.cpu as CPU_LOW)!.InlineInterruptChecks = false; } catch {}
				try {
					while (globalCpuCycle < frameEndCycle)
					{
						long next = frameEndCycle;
						if (nextPpuEventCycle > globalCpuCycle && nextPpuEventCycle < next) next = nextPpuEventCycle;
						if (nextApuEventCycle > globalCpuCycle && nextApuEventCycle < next) next = nextApuEventCycle;
						if (nextIrqCycle > globalCpuCycle && nextIrqCycle < next) next = nextIrqCycle;
						long remaining = next - globalCpuCycle;
						int batchCpu = 0;
						// Execute instructions until we reach or exceed the next event boundary
						while (batchCpu < remaining)
						{
							int cpuCycles = bus.cpu.ExecuteInstruction();
							batchCpu += cpuCycles;
							executed += cpuCycles;
							// Safety: prevent huge single batches if a very long instruction sequence occurs (unlikely)
							if (batchCpu >= ConfigMaxEventLoopInstructionBurstCycles) break;
						}
						// Service pending interrupts at boundary (adds their cycles to batch before flush)
						int irqCycles = (bus.cpu as CPU_LOW)!.ServicePendingInterrupts();
						if (irqCycles > 0) { batchCpu += irqCycles; executed += irqCycles; }
						FlushBatch(batchCpu);
						// Process PPU scanline event if reached
						if (globalCpuCycle >= nextPpuEventCycle) ScheduleNextPpuScanline();
						if (globalCpuCycle >= nextApuEventCycle) ScheduleNextApuEvent();
						if (globalCpuCycle >= nextIrqCycle) { nextIrqCycle = long.MaxValue; } // simple one-shot until real scheduling
					}
				}
				catch (CPU_FMC.CpuCrashException ex) { HandleCpuCrash(ex); return; }
			}
			else
			{
				// --- Legacy batch heuristic path (current default) ---
				int batchCpu = 0;
				if (nextPpuEventCycle < globalCpuCycle) nextPpuEventCycle = globalCpuCycle; // keep monotonic (placeholder)
				try {
					while (globalCpuCycle < frameEndCycle)
					{
						for (int i = 0; i < ConfigMaxInstructionsPerBatch && globalCpuCycle < frameEndCycle; i++)
						{
							int cpuCycles = bus.cpu.ExecuteInstruction();
							executed += cpuCycles;
							batchCpu += cpuCycles;
							if (batchCpu >= ConfigBatchCycleThreshold)
							{
								FlushBatch(batchCpu);
								batchCpu = 0;
							}
						}
						if (batchCpu > 0 && (globalCpuCycle + batchCpu >= frameEndCycle || (frameEndCycle - (globalCpuCycle + batchCpu)) <= ConfigMinRemainingFlushGuard))
						{
							FlushBatch(batchCpu);
							batchCpu = 0;
						}
					}
				}
				catch (CPU_FMC.CpuCrashException ex) { HandleCpuCrash(ex); return; }
			}
			// (Exceptions already handled within each scheduling branch)
			// If we executed beyond the frame target (shouldn't with frameEndCycle guard) track overshoot for legacy compatibility
			if (executed > targetCycles) overshootCarry = executed - targetCycles; else overshootCarry = 0;
			// Always update frame buffer (no frameskip) for smoother perceived motion
			if (!crashed) bus.ppu.UpdateFrameBuffer();
		}

		// --- Batch scheduler configuration (Item #1 partial) ---
		// Maximum instructions to execute before forcing a flush even if cycle threshold not reached.
		private const int ConfigMaxInstructionsPerBatch = 32; // tune experimentally
		// Cycle threshold: once accumulated CPU cycles exceed this, we flush to PPU/APU.
		private const int ConfigBatchCycleThreshold = 24; // allows combining several short (2-3 cycle) ops
		// If remaining cycles to frame end are below this guard after a loop, flush leftover to keep timing bounded.
		private const int ConfigMinRemainingFlushGuard = 16;
		// Limit for a single event-loop CPU instruction burst to avoid extremely large batches when events are sparse.
		private const int ConfigMaxEventLoopInstructionBurstCycles = 1024; // conservative; tuned later
		// Global absolute CPU cycle counter (monotonic across frames) for upcoming event scheduler.
		private long globalCpuCycle = 0;
		// Event scheduler scaffolding fields (will be populated by PPU/APU once they provide event times)
		private long nextPpuEventCycle = 0;
		private long nextApuEventCycle = 0;
		private long nextIrqCycle = long.MaxValue; // placeholder until IRQ scheduling implemented (#17)
		private long nextFrameBoundaryCycle = 0;
		// Feature flag to toggle experimental event loop
		public bool EnableEventScheduler { get; set; } = false;
		// PPU scanline scheduling pattern (114,114,113) CPU cycles approximates 341 PPU cycles per scanline (341/3=113.667)
		private static readonly int[] PpuScanlineCpuPattern = new int[]{114,114,113};
		private int ppuScanlinePatternIndex = 0;
		private void ScheduleNextPpuScanline()
		{
			int delta = PpuScanlineCpuPattern[ppuScanlinePatternIndex];
			ppuScanlinePatternIndex++; if (ppuScanlinePatternIndex >= PpuScanlineCpuPattern.Length) ppuScanlinePatternIndex = 0;
			nextPpuEventCycle = globalCpuCycle + delta;
		}

		// APU event scheduling (placeholder granularity: every 1 CPU cycle batch up to small quantum)
		// Future: expose fine-grained events (frame sequencer, DMC IRQ) from active APU core.
		private const int ApuEventQuantum = 64; // re-schedule APU mixing/check every 64 CPU cycles
		private void ScheduleNextApuEvent()
		{
			long target = globalCpuCycle + ApuEventQuantum;
			if (target <= globalCpuCycle) target = globalCpuCycle + 1;
			nextApuEventCycle = target;
		}
		// Consolidated flush helper so later event-based stepping can reuse it
		private void FlushBatch(int cpuCycles)
		{
			// Advance subsystems for accumulated cycles; order: PPU (3x), APU, then advance global cycle.
			bus.ppu.Step(cpuCycles * 3);
			bus.StepAPU(cpuCycles);
			bus.CountBatchFlush();
			globalCpuCycle += cpuCycles;
		}
		private void HandleCpuCrash(CPU_FMC.CpuCrashException ex)
		{
			if (crashBehavior == CrashBehavior.IgnoreErrors)
			{
				try { bus.cpu.AddToPC(1); } catch {}
				return;
			}
			crashed = true;
			var regsCrash = bus.cpu.GetRegisters();
			crashInfo = ex.Message + " PC=" + regsCrash.PC.ToString("X4");
			RenderCrashScreen();
		}

		// Allow caller to rapidly run multiple frames (used for fast-forward / warm-up)
		public void RunFrames(int frames)
		{
			for (int i = 0; i < frames; i++) RunFrame();
		}

		// === Instrumentation & Benchmarks (Theory #38) ===
		private long framesExecutedTotal = 0;
		public record BenchResult(string Name, int Iterations, double MsTotal, double MsPerIter, long CpuReads, long CpuWrites, long ApuCycles, long OamDmaWrites, long BatchFlushes)
		{
			public double AvgBatchSize => BatchFlushes > 0 ? (double)ApuCycles / BatchFlushes : 0.0; // APU cycles == CPU cycles stepped in batches
		}
		public IReadOnlyList<BenchResult> RunBenchmarks(int weight = 1)
		{
			if (weight <= 0) weight = 1; // clamp
			var list = new System.Collections.Generic.List<BenchResult>();
			if (bus == null) return list;
			// Helper local function
			BenchResult Run(string name, System.Action body, int iters)
			{
				bus.ResetInstrumentation();
				var sw = System.Diagnostics.Stopwatch.StartNew();
				for (int i=0;i<iters;i++) body();
				sw.Stop();
				var instr = bus.GetInstrumentation();
				return new BenchResult(name, iters, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds/iters, instr.Reads, instr.Writes, instr.ApuSteps, instr.OamDmaWrites, instr.BatchFlushes);
			}
			try {
				// 1. Run N frames baseline (uses current ROM & state)
				int frameIters = 120 * weight;
				list.Add( Run($"Frame({frameIters})", () => RunFrame(), frameIters) );
				// 2. CPU instruction burst (simulate by executing slices until ~target instructions)
				if (bus?.cpu != null)
				{
					int targetInstr = 10_000 * weight;
					list.Add( Run($"Instr({targetInstr/1000}k)", () => {
						int executed=0; while (executed < targetInstr) { int cyc = bus.cpu.ExecuteInstruction(); executed += cyc; bus.ppu.Step(cyc*3); bus.StepAPU(cyc); }
					}, 10)); // keep iterations constant for stable averaging
				}
				// 3. Audio pacing: approximate N seconds (weight seconds)
				list.Add( Run($"Audio({weight}s est)", () => {
					long targetCycles=(long)CpuFrequency * weight; long done=0; while(done<targetCycles){ int c=bus.cpu.ExecuteInstruction(); done+=c; bus.ppu.Step(c*3); bus.StepAPU(c);} }, 1));
			}
			catch { }
			return list;
		}
		public string FormatBenchmarksForDisplay(IReadOnlyList<BenchResult> results)
		{
			var sb = new System.Text.StringBuilder();
			sb.AppendLine("Benchmark Results");
			sb.AppendLine("Target Cat\tIter\tTot(ms)\tPer(ms)\tReads\tWrites\tAPU Cyc\tOAM DMA\tBatches\tAvgBatch");
			foreach (var r in results)
			{
				sb.AppendLine($"{r.Name}\t{r.Iterations}\t{r.MsTotal:F2}\t{r.MsPerIter:F3}\t{r.CpuReads}\t{r.CpuWrites}\t{r.ApuCycles}\t{r.OamDmaWrites}\t{r.BatchFlushes}\t{r.AvgBatchSize:F1}");
			}
			return sb.ToString();
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
			{
				// Soft-flush policy: if backlog exceeds ~6k samples (~136ms @44.1kHz), drop oldest to recover.
				int queued = bus.GetQueuedSamples();
				const int backlogSoftCap = 6144;
				if (queued > backlogSoftCap)
				{
					int toDrop = queued - backlogSoftCap;
					// Drain in a single allocation instead of multiple 2k chunks to reduce GC pressure.
					_ = bus.GetAudioSamples(toDrop);
				}
				return bus.GetAudioSamples(2048);
			}
			return Silent2048;
		}

		private static readonly float[] Silent2048 = new float[2048];

		public int GetQueuedAudioSamples() => bus?.GetQueuedSamples() ?? 0;
		public int GetAudioSampleRate() => bus?.GetAudioSampleRate() ?? 44100;

		// ===== SoundFont / Note Event Mode (APU_WF only) =====
		private bool soundFontEnabled = false;
		private Action<NesEmulator.APU_WF.NesNoteEvent>? noteSub; // retain delegate reference for unsubscribe
		public void FlushSoundFont()
		{
			if (bus?.ActiveAPU is NesEmulator.APU_WF wf)
			{
				try { wf.SoundFontMode = false; } catch { }
				if (noteSub != null) { try { wf.NoteEvent -= noteSub; } catch { } noteSub = null; }
				soundFontEnabled = false;
			}
		}
		public bool EnableSoundFontMode(bool enable, System.Action<string,int,int,int,bool,int>? noteCallback = null)
		{
			if (bus?.ActiveAPU is not NesEmulator.APU_WF wf)
			{
				// If switching away from WF core, disable and detach
				soundFontEnabled = false;
				return false;
			}
			if (enable == soundFontEnabled) return soundFontEnabled;
			soundFontEnabled = enable;
			if (enable)
			{
				wf.SoundFontMode = true;
				// Subscribe once
				noteSub = (ev) => {
					try { noteCallback?.Invoke(ev.Channel, ev.Program, ev.MidiNote, ev.Velocity, ev.On, 0); } catch { }
				};
				wf.NoteEvent += noteSub;
			}
			else
			{
				wf.SoundFontMode = false;
				if (noteSub != null) try { wf.NoteEvent -= noteSub; } catch { }
				noteSub = null;
			}
			return soundFontEnabled;
		}

		// Removed famicloneMode boolean API; UI should query active APU id or GetApuCore()

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
