-- The live reference for the BrokenNes MCP server's play_step tool (BrokenDev\mcp\Reference.cs): Mesen 2 driven in lock step over TCP.
-- Started as: Mesen.exe <rom> --luaScriptsToLoad mesen_refstep.lua --testRunner --timeout=<s>, with BNREF_PORT (the server's port on
-- 127.0.0.1), BNREF_SHOT (the file the picture is written to) and BNREF_CONSOLE (nes snes gb gbc sms gg). Needs the settings
-- Debug.ScriptWindow.AllowIoOsAccess and AllowNetworkAccess (then require("socket.core") is LuaSocket's core).
--
-- Protocol (one line each way): the server sends "run <target> <pad1 mask> <pad2 mask> <shot 0|1|2 (2: half size)> <addr,addr,...|->"; the script holds the
-- pads (PadButtons bits) until Mesen has completed <target> frames since power-on (at once when it already has), then answers
-- "ok <frames done> <value> <value> ...". Absolute targets: Mesen may run its first frame before the script gets control, and the server
-- keeps the alignment from the frame counts it is told. Mesen is parked inside its endFrame
-- callback while it waits, so it never runs ahead of BrokenNes. "quit" ends the run. The picture: "<w> <h>\n" then w*h little-endian
-- 0xRRGGBB words (emu.getScreenBuffer: the frame just finished, unlike takeScreenshot, which can lag on the SNES).
local core = require("socket.core")
local port = tonumber(os.getenv("BNREF_PORT") or "")
local shotPath = os.getenv("BNREF_SHOT")
local console = os.getenv("BNREF_CONSOLE") or "nes"
if not port or not shotPath then emu.log("refstep: BNREF_PORT / BNREF_SHOT missing"); emu.stop(3); return end

local c = core.tcp()
c:settimeout(20)
local okc, errc = c:connect("127.0.0.1", port)
if not okc then emu.log("refstep: connect failed " .. tostring(errc)); emu.stop(3); return end
c:settimeout(nil)

local mem = ({ nes = emu.memType.nesDebug, snes = emu.memType.snesDebug, gb = emu.memType.gameboyDebug, gbc = emu.memType.gameboyDebug,
               sms = emu.memType.smsDebug, gg = emu.memType.smsDebug })[console]
-- PadButtons bit -> Mesen's key name (Up Down Left Right A B X Y L R Start Select C Z Mode)
local bits = { "up", "down", "left", "right", "a", "b", "x", "y", "l", "r", "start", "select", "c", "z", "mode" }
if console == "sms" then bits[5] = "one"; bits[6] = "two"; bits[12] = "pause"; bits[15] = "reset" end
if console == "gg" then bits[5] = "one"; bits[6] = "two"; bits[11] = "start" end

local function pad(mask)
  local t = {}
  for i, n in ipairs(bits) do if (mask >> (i - 1)) & 1 == 1 then t[n] = true end end
  return t
end

local done, target, started = 0, 0, false
local p1, p2, shot, half, addrs = 0, 0, false, false, {}
local pad1, pad2 = {}, {}

local packs = {}
local function packer(n)
  local f = packs[n]
  if not f then f = "<" .. string.rep("I4", n); packs[n] = f end
  return f
end

-- half: every other pixel of every other line (the server's align compares half-size pictures)
local function writeShot(half)
  local buf = emu.getScreenBuffer()
  local sz = emu.getScreenSize and emu.getScreenSize() or nil
  local w = sz and sz.width or 256
  local h = sz and sz.height or (#buf // w)
  local parts = {}
  local ok = pcall(function()
    if half then
      local hw, hh, row = w // 2, h // 2, {}
      for y = 0, hh - 1 do
        local base = y * 2 * w
        for x = 1, hw do row[x] = buf[base + x * 2 - 1] & 0xFFFFFF end
        parts[#parts + 1] = string.pack(packer(hw), table.unpack(row, 1, hw))
      end
      w, h = hw, hh
    else
      local n = w * h
      for i = 1, n, 256 do
        local k = math.min(256, n - i + 1)
        parts[#parts + 1] = string.pack(packer(k), table.unpack(buf, i, i + k - 1))
      end
    end
  end)
  if not ok then
    -- a pixel string.pack refused (out of range): one by one, masked
    parts = {}
    for i = 1, w * h do parts[i] = string.pack("<I4", (buf[i] or 0) & 0xFFFFFF) end
  end
  local f = io.open(shotPath .. ".tmp", "wb")
  f:write(w .. " " .. h .. "\n")
  f:write(table.concat(parts))
  f:close()
  os.remove(shotPath)
  os.rename(shotPath .. ".tmp", shotPath)
end

local function reply()
  local vals = {}
  for i, a in ipairs(addrs) do vals[i] = tostring(emu.read(a, mem)) end
  if shot then writeShot(half) end
  c:send("ok " .. done .. (#vals > 0 and (" " .. table.concat(vals, " ")) or "") .. "\n")
end

-- returns true when frames are to run, false when the run ends
local function waitCommand()
  while true do
    local line = c:receive("*l")
    if not line then emu.stop(0); return false end
    local cmd, n, m1, m2, s, list = line:match("^(%S+)%s*(%d*)%s*(%d*)%s*(%d*)%s*(%d*)%s*(%S*)")
    if cmd == "quit" then c:close(); emu.stop(0); return false end
    if cmd == "run" then
      p1, p2, shot, half = tonumber(m1) or 0, tonumber(m2) or 0, s == "1" or s == "2", s == "2"
      pad1, pad2 = pad(p1), pad(p2)
      addrs = {}
      if list and list ~= "" and list ~= "-" then for a in list:gmatch("[^,]+") do addrs[#addrs + 1] = tonumber(a) end end
      target = tonumber(n) or 0
      if target > done then return true end
      reply()
    else
      c:send("error unknown command\n")
    end
  end
end

-- control is taken at the first event Mesen gives the script (the start of a frame, a pad poll or the end of the first frame); the
-- server reads the frames already done from the hello line and keeps its alignment from them
local function start()
  if started then return end
  started = true
  local keys = {}
  for k, _ in pairs(emu.getInput(0) or {}) do keys[#keys + 1] = k end
  table.sort(keys)
  c:send("hello mesen " .. console .. " done=" .. done .. " keys=" .. table.concat(keys, ",") .. "\n")
  waitCommand()
end

emu.addEventCallback(start, emu.eventType.startFrame)

emu.addEventCallback(function()
  start()
  emu.setInput(pad1, 0)
  emu.setInput(pad2, 1)
end, emu.eventType.inputPolled)

emu.addEventCallback(function()
  done = done + 1
  if not started then start() return end
  if done >= target then reply(); waitCommand() end
end, emu.eventType.endFrame)
