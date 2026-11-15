-- The live reference for the BrokenNes MCP server's play_step tool (BrokenDev\mcp\Reference.cs): BizHawk (EmuHawk) driven in lock step
-- over its socket server (EmuHawk --socket_ip=127.0.0.1 --socket_port=<the server's port> --lua=bizhawk_refstep.lua; EmuHawk connects to
-- the server). Used for the Genesis (Genplus-gx), and for any console the server is told to check on BizHawk.
-- Environment: BNREF_SHOT (the picture file), BNREF_CONSOLE (nes snes gb gbc sms gg md).
--
-- Messages are BizHawk's socket framing both ways: "<length> <text>". The server sends "run <target> <pad1 mask> <pad2 mask> <shot 0|1>
-- <addr,addr,...|->": the pads (PadButtons bits) are set before every frame until emu.framecount() reaches <target>, then the answer is
-- "ok <framecount> <value> ...". The script is in control between frames (emu.frameadvance), so EmuHawk never runs ahead. "quit" ends.
-- The picture: client.screenshot (the core's frame buffer as PNG, no OSD: the config sets ScreenshotCaptureOsd off).
local shotPath = os.getenv("BNREF_SHOT")
local console = os.getenv("BNREF_CONSOLE") or "md"

-- EmuHawk starts paused (the config's StartPaused): frame 0. Otherwise restart the core for a real power-on.
if emu.framecount() ~= 0 then client.reboot_core() end
if client.ispaused() then client.unpause() end

-- PadButtons bit order: Up Down Left Right A B X Y L R Start Select C Z Mode
local canon = { "Up", "Down", "Left", "Right", "A", "B", "X", "Y", "L", "R", "Start", "Select", "C", "Z", "Mode" }
if console == "md" then canon[9] = "Z"; canon[10] = "C"; canon[12] = "Mode" end   -- MdSession's generic layout: L -> Z, R -> C, Select -> Mode
if console == "sms" or console == "gg" then canon[5] = "B1"; canon[6] = "B2"; canon[12] = "Pause" end

local have = {}
for k, _ in pairs(joypad.get() or {}) do have[tostring(k)] = true end
local function nameOf(player, b)
  local n = "P" .. player .. " " .. b
  if have[n] then return n end
  if player == 1 and have[b] then return b end
  return nil
end
local names = { {}, {} }
for p = 1, 2 do for i, b in ipairs(canon) do names[p][i] = nameOf(p, b) end end

local function pads(m1, m2)
  local t = {}
  for p, m in ipairs({ m1, m2 }) do
    for i = 1, #canon do
      local n = names[p][i]
      if n then t[n] = ((m >> (i - 1)) & 1) == 1 end
    end
  end
  return t
end

local function read(a)
  if console == "md" then return mainmemory.read_u8(a & 0xFFFF) end
  return memory.read_u8(a, "System Bus")
end

comm.socketServerSetTimeout(600000)
local keys = {}
for k, _ in pairs(have) do keys[#keys + 1] = k end
table.sort(keys)
comm.socketServerSend("hello bizhawk " .. console .. " done=" .. emu.framecount() .. " core=" .. tostring(emu.getsystemid()) .. " keys=" .. table.concat(keys, ","):gsub(" ", "_"))

while true do
  local line = comm.socketServerResponse()
  if line == nil or line == "" then
    if not comm.socketServerIsConnected() then break end
  else
    local cmd, n, m1, m2, s, list = line:match("^(%S+)%s*(%d*)%s*(%d*)%s*(%d*)%s*(%d*)%s*(%S*)")
    if cmd == "quit" then break end
    if cmd == "run" then
      local target, p1, p2 = tonumber(n) or 0, tonumber(m1) or 0, tonumber(m2) or 0
      local t = pads(p1, p2)
      while emu.framecount() < target do
        joypad.set(t)
        emu.frameadvance()
      end
      local vals = {}
      if list and list ~= "" and list ~= "-" then for a in list:gmatch("[^,]+") do vals[#vals + 1] = tostring(read(tonumber(a))) end end
      if s == "1" or s == "2" then client.screenshot(shotPath) end
      comm.socketServerSend("ok " .. emu.framecount() .. (#vals > 0 and (" " .. table.concat(vals, " ")) or ""))
    else
      comm.socketServerSend("error unknown command")
    end
  end
end
client.exitCode(0)
