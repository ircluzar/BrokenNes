-- ============================================================================
--  How far behind the CPU is Mesen's APU, and is there ANY instant where it
--  is exactly caught up?
-- ============================================================================
--  apu.frameCounter.previousCycle is written by ApuFrameCounter::Run(target),
--  which NesApu::Run() calls - so it is a direct read-out of "how far the APU
--  has actually been advanced". Sampling it next to cpu.cycleCount at a dense
--  set of instants shows the lag directly:
--
--    * if the APU is caught up at instant X, previousCycle advances by exactly
--      the same number of cycles the CPU did between two samples;
--    * if it is lazy, previousCycle sits still for long stretches and then
--      jumps by a large chunk when something forces a Run().
--
--  Two instants are sampled, because they are the only two a tracer could ever
--  use:
--    (a) EXEC  - every instruction boundary. The densest hook Lua offers.
--    (b) WRITE - the moment the CPU writes an APU register ($4000-$4017).
--      A register write is the one event that Mesen documents as forcing a
--      catch-up, so if the APU is exact anywhere, it is exact here.
--
--  Whichever of the two is exact defines the sampling grid the whole audio
--  comparison can be built on. If neither is, the 15 phase keys really are
--  unreachable and the comparison has to be built some other way.
--
--  ENV  VRUN_LAG_OUT, VRUN_LAG_FRAME (first frame to sample), VRUN_LAG_SPAN
-- ============================================================================

local OUT = os.getenv("VRUN_LAG_OUT") or "apu_lag_probe.txt"
local AT = tonumber(os.getenv("VRUN_LAG_FRAME") or "") or 250
local SPAN = tonumber(os.getenv("VRUN_LAG_SPAN") or "") or 2

local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end
local function w(...) f:write(...) f:write("\n") f:flush() end

local frame = 0
emu.addEventCallback(function() frame = frame + 1 end, emu.eventType.endFrame)

local function active() return frame >= AT and frame < AT + SPAN end

w("# kind|frame|cpuCycle|apuFrameCounterPreviousCycle|addr|value|sq1timer|sq1dutyPos|sq1lastOut|noiseShift|triSeqPos|dmcOut")

local function sample(kind, addr, value)
	local s = emu.getState()
	w(kind, "|", frame,
	  "|", s["cpu.cycleCount"],
	  "|", s["apu.frameCounter.previousCycle"],
	  "|", addr or "-",
	  "|", value or "-",
	  "|", s["apu.square1.timer.timer"],
	  "|", s["apu.square1.dutyPos"],
	  "|", s["apu.square1.timer.lastOutput"],
	  "|", s["apu.noise.shiftRegister"],
	  "|", s["apu.triangle.sequencePosition"],
	  "|", s["apu.dmc.outputLevel"])
end

-- (a) instruction boundaries. Sampled for a bounded number of instructions so
-- the output file stays readable; the lag pattern is obvious within a frame.
local execCount = 0
emu.addMemoryCallback(function(addr)
	if not active() then return end
	execCount = execCount + 1
	if execCount > 4000 then return end
	sample("exec", addr, nil)
end, emu.callbackType.exec, 0x0000, 0xFFFF, emu.cpuType.nes)

-- (b) APU register writes.
emu.addMemoryCallback(function(addr, value)
	if not active() then return end
	sample("apuwrite", addr, value)
end, emu.callbackType.write, 0x4000, 0x4017, emu.cpuType.nes)

emu.addEventCallback(function()
	if frame >= AT + SPAN then f:close() emu.stop(0) end
end, emu.eventType.endFrame)
