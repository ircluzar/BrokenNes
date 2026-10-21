-- DIAGNOSTIC ONLY. Answers one question: which addMemoryCallback registration actually fires on
-- this Mesen build? The earlier ppu2007_log.lua registered a $2007 write callback and captured
-- ZERO rows, which is either (a) the wrong argument shape, (b) the wrong memory type, or (c) the
-- callback firing but the frame filter eating everything. This counts every variant side by side
-- over the first few frames so the answer is measured instead of guessed.

local OUT = os.getenv("PROBE_OUT") or "ppu2007_probe.txt"
local STOP_FRAME = tonumber(os.getenv("PROBE_FRAMES") or "12")

local counts = {}
local samples = {}
local function bump(tag, s)
  counts[tag] = (counts[tag] or 0) + 1
  if counts[tag] <= 3 then
    samples[tag] = samples[tag] or {}
    table.insert(samples[tag], s)
  end
end

local errs = {}
local function try(tag, fn)
  local ok, err = pcall(fn)
  if not ok then errs[#errs + 1] = tag .. ": " .. tostring(err) end
end

-- Variant 1: exactly what ppu2007_log.lua did - 4 args, default cpu/mem type.
try("v1", function()
  emu.addMemoryCallback(function(addr, value)
    bump("v1_cpuwrite_2007", string.format("%04x=%02x", addr, value))
  end, emu.callbackType.write, 0x2007, 0x2007)
end)

-- Variant 2: same but the whole PPU register window, in case $2007 alone was mirrored elsewhere.
try("v2", function()
  emu.addMemoryCallback(function(addr, value)
    bump("v2_cpuwrite_2000_3fff", string.format("%04x=%02x", addr, value))
  end, emu.callbackType.write, 0x2000, 0x3FFF)
end)

-- Variant 3: the entire CPU address space. If THIS is zero the callback plumbing itself is dead.
try("v3", function()
  emu.addMemoryCallback(function(addr, value)
    bump("v3_cpuwrite_all", string.format("%04x=%02x", addr, value))
  end, emu.callbackType.write, 0x0000, 0xFFFF)
end)

-- Variant 4: explicit cpuType + memType, the 6-arg form.
try("v4", function()
  emu.addMemoryCallback(function(addr, value)
    bump("v4_explicit_nesMemory_2007", string.format("%04x=%02x", addr, value))
  end, emu.callbackType.write, 0x2007, 0x2007, emu.cpuType.nes, emu.memType.nesMemory)
end)

-- Variant 5: THE PRIZE. A callback on CIRAM itself reports the address and byte that actually
-- landed, which is precisely the ground truth the $2007-during-rendering rule has to reproduce.
try("v5", function()
  emu.addMemoryCallback(function(addr, value)
    bump("v5_ntram_write", string.format("%04x=%02x", addr, value))
  end, emu.callbackType.write, 0x0000, 0x07FF, emu.cpuType.nes, emu.memType.nesNametableRam)
end)

-- Variant 6: the PPU bus view ($0000-$3FFF as the PPU sees it).
try("v6", function()
  emu.addMemoryCallback(function(addr, value)
    bump("v6_ppubus_write", string.format("%04x=%02x", addr, value))
  end, emu.callbackType.write, 0x0000, 0x3FFF, emu.cpuType.nes, emu.memType.nesPpuMemory)
end)

local frame = 0
emu.addEventCallback(function()
  frame = frame + 1
  if frame < STOP_FRAME then return end
  local f = io.open(OUT, "w")
  f:write("frames=", frame, "\n\n== registration errors ==\n")
  if #errs == 0 then f:write("(none)\n") end
  for _, e in ipairs(errs) do f:write(e, "\n") end

  f:write("\n== counts ==\n")
  local keys = {}
  for k in pairs(counts) do keys[#keys + 1] = k end
  table.sort(keys)
  for _, k in ipairs(keys) do
    f:write(string.format("%-32s %d   first: %s\n", k, counts[k],
      table.concat(samples[k] or {}, " ")))
  end
  for _, k in ipairs({ "v1_cpuwrite_2007", "v2_cpuwrite_2000_3fff", "v3_cpuwrite_all",
                       "v4_explicit_nesMemory_2007", "v5_ntram_write", "v6_ppubus_write" }) do
    if counts[k] == nil then f:write(string.format("%-32s 0\n", k)) end
  end

  f:write("\n== emu.callbackType ==\n")
  local ck = {}
  for k, v in pairs(emu.callbackType) do ck[#ck + 1] = k .. "=" .. tostring(v) end
  table.sort(ck)
  f:write(table.concat(ck, " "), "\n")

  f:write("\n== emu.cpuType ==\n")
  local cp = {}
  for k, v in pairs(emu.cpuType) do cp[#cp + 1] = k .. "=" .. tostring(v) end
  table.sort(cp)
  f:write(table.concat(cp, " "), "\n")

  -- What does a memory callback see for PPU state? Confirm the dotted keys resolve.
  local s = emu.getState()
  f:write("\n== ppu state keys ==\n")
  local pk = {}
  for k in pairs(s) do if k:sub(1, 4) == "ppu." then pk[#pk + 1] = k end end
  table.sort(pk)
  f:write(table.concat(pk, "\n"), "\n")

  f:close()
  emu.stop(0)
end, emu.eventType.endFrame)
