-- ============================================================================
--  Can Mesen's Lua API be made to report a CAUGHT-UP APU?
-- ============================================================================
--  THE PROBLEM THIS EXISTS TO SETTLE
--    Mesen runs NesApu lazily: the channels are only advanced to the current
--    CPU cycle when something forces it (a register write, a $4015 read, the
--    end of a frame). emu.getState() does NOT force it, so the 15 phase-
--    carrying apu.* keys - duty position, triangle sequence position, the
--    noise LFSR, every timer.timer, and every timer.lastOutput (which IS the
--    channel's output sample) - come back stale by a variable number of
--    cycles. That variability is why they were excluded from the frame trace
--    and why audio has never been compared at all.
--
--    The existing note in Workshop/TraceCli.cs concludes "no side-effect-free
--    catch-up exists in that Lua API". This script tests that conclusion
--    against every mechanism the API actually offers, one at a time, each in
--    its own savestate-isolated trial so a method with side effects cannot
--    contaminate the next.
--
--  METHOD
--    At a frame deep enough to have music playing, snapshot the machine, then
--    for each candidate: apply it, diff every apu.* key against the untouched
--    baseline, and diff the NON-apu keys too - because a "catch-up" that also
--    moves the CPU, the PPU or an IRQ flag is a perturbation of the run being
--    traced, not an observation of it. Reload the snapshot between trials.
--
--    A candidate PASSES only if
--      (a) it advances apu.frameCounter.previousCycle to the current CPU cycle
--          (i.e. it really did run the APU), and
--      (b) the only keys it moves are the 15 phase keys, and
--      (c) it moves nothing outside apu.*.
--
--  ENV  VRUN_PROBE_OUT (default apu_catchup_probe.txt), VRUN_PROBE_FRAME (300)
-- ============================================================================

local OUT = os.getenv("VRUN_PROBE_OUT") or "apu_catchup_probe.txt"
local AT_FRAME = tonumber(os.getenv("VRUN_PROBE_FRAME") or "") or 300
local f = io.open(OUT, "wb")
if not f then print("cannot open " .. OUT) emu.stop(5) return end
-- Flush on every line. A --testRunner process shows no console output and an
-- uncaught Lua error just ends the script, so an unflushed buffer is the
-- difference between a diagnosis and a zero-byte file.
local function w(...) f:write(...) f:write("\n") f:flush() end

local function snapshotKeys()
	local s = emu.getState()
	local t = {}
	for k, v in pairs(s) do t[tostring(k)] = v end
	return t
end

-- Returns a sorted list of "key old -> new" for every key whose value differs.
local function diffKeys(a, b)
	local out = {}
	local all = {}
	for k, _ in pairs(a) do all[k] = true end
	for k, _ in pairs(b) do all[k] = true end
	local keys = {}
	for k, _ in pairs(all) do keys[#keys + 1] = k end
	table.sort(keys)
	for _, k in ipairs(keys) do
		if a[k] ~= b[k] then
			out[#out + 1] = k .. "  " .. tostring(a[k]) .. " -> " .. tostring(b[k])
		end
	end
	return out
end

-- Every candidate that could plausibly force NesApu::Run(). Each is wrapped in
-- pcall because a wrong arity for this build must be reported, not fatal.
local candidates = {
	{ name = "emu.read(0x4015, nesMemory)",
	  fn = function() return emu.read(0x4015, emu.memType.nesMemory) end },
	{ name = "emu.read(0x4015, nesMemory, true)  -- sendToDebugger",
	  fn = function() return emu.read(0x4015, emu.memType.nesMemory, true) end },
	{ name = "emu.read(0x4015, nesDebug)",
	  fn = function() return emu.read(0x4015, emu.memType.nesDebug) end },
	{ name = "emu.write(0x4009, 0, nesMemory)  -- unused APU reg",
	  fn = function() return emu.write(0x4009, 0, emu.memType.nesMemory) end },
	{ name = "emu.write(0x400D, 0, nesMemory)  -- unused APU reg",
	  fn = function() return emu.write(0x400D, 0, emu.memType.nesMemory) end },
	{ name = "emu.write(0x4009, 0, nesMemory, false)  -- side effects on",
	  fn = function() return emu.write(0x4009, 0, emu.memType.nesMemory, false) end },
	{ name = "emu.write(0x400D, 0, nesMemory, false)  -- side effects on",
	  fn = function() return emu.write(0x400D, 0, emu.memType.nesMemory, false) end },
	{ name = "emu.read(0x4000, nesMemory)  -- write-only reg, open bus",
	  fn = function() return emu.read(0x4000, emu.memType.nesMemory) end },
	{ name = "emu.getState() twice (is getState itself a catch-up?)",
	  fn = function() local _ = emu.getState() return "n/a" end },
	{ name = "emu.step(1, cpuCycleStep)  -- control: advances the machine",
	  fn = function() return emu.step(1, emu.stepType.cpuCycleStep) end },
}

local done = false
local n = 0

local function body()
	w("# APU catch-up probe at frame ", n)
	local base = snapshotKeys()
	w("# baseline cpu.cycleCount        = ", tostring(base["cpu.cycleCount"]))
	w("# baseline apu.frameCounter.previousCycle = ", tostring(base["apu.frameCounter.previousCycle"]))
	w("")

	-- The savestate is what makes each trial independent; without it the first
	-- candidate with a side effect poisons every candidate after it.
	local st = emu.createSavestate()

	for _, c in ipairs(candidates) do
		local ok, res = pcall(c.fn)
		local after = snapshotKeys()
		local d = diffKeys(base, after)
		w("=== ", c.name)
		w("    call ok=", tostring(ok), "  result=", tostring(res))
		local apuMoved, otherMoved = 0, 0
		for _, line in ipairs(d) do
			if line:sub(1, 4) == "apu." then apuMoved = apuMoved + 1 else otherMoved = otherMoved + 1 end
		end
		w("    keys changed: ", tostring(#d), "  (apu.* = ", tostring(apuMoved),
		  ", outside apu.* = ", tostring(otherMoved), ")")
		for _, line in ipairs(d) do w("      ", line) end
		w("")
		emu.loadSavestate(st)
	end

end

-- The probe has to run inside an EXEC memory callback, not endFrame: Mesen
-- refuses emu.createSavestate() anywhere else ("This function must be called
-- inside an exec memory operation callback for the main CPU"), and without a
-- savestate the trials cannot be isolated from each other. Running here is
-- also informative in its own right - it tells us what the APU lag looks like
-- at an instruction boundary, which is the only place a dense trace could
-- ever sample from.
emu.addEventCallback(function() n = n + 1 end, emu.eventType.endFrame)

emu.addMemoryCallback(function()
	if done or n < AT_FRAME then return end
	done = true
	local ok, err = pcall(body)
	if not ok then w("FATAL inside probe body: ", tostring(err)) end
	f:close()
	emu.stop(ok and 0 or 6)
end, emu.callbackType.exec, 0x0000, 0xFFFF, emu.cpuType.nes)
