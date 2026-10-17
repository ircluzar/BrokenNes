// Loads the .NET WebAssembly floor build and times it: boot, first sound block, and a steady-state render.
import { dotnet } from './_framework/dotnet.js';

const t0 = performance.now();
const { getAssemblyExports, getConfig } = await dotnet.withDiagnosticTracing(false).create();
const tBoot = performance.now();
const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);
const floor = exports.Floor;
floor.Init(48000);
floor.Write(0x4000, 0xBF); floor.Write(0x4002, 0xFD); floor.Write(0x4003, 0xF8);
floor.Write(0x4004, 0x7F); floor.Write(0x4006, 0x80); floor.Write(0x4007, 0xF9);
floor.Write(0x4008, 0xFF); floor.Write(0x400A, 0x40); floor.Write(0x400B, 0xF8);
floor.Write(0x400C, 0x3F); floor.Write(0x400E, 3); floor.Write(0x400F, 0xF8);
const peak = floor.RenderBlock();
const tFirst = performance.now();
const results = [];
for (let i = 0; i < 4; i++) results.push(floor.Bench(48000, 5) / 5);
results.sort((a, b) => a - b);
const out = { bootMs: tBoot - t0, firstBlockAfterBootMs: tFirst - tBoot, firstBlockPeak: peak, msPerAudioSecond: results, navToFirstBlockMs: tFirst };
window.floorResult = out;
document.getElementById('out').textContent = JSON.stringify(out, null, 1);
