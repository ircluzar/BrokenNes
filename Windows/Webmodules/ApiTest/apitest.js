// Web API Test Suite for BrokenNes
const api = window.webapi;

// Utility: Display result
function displayResult(elementId, data, isError = false) {
  const el = document.getElementById(elementId);
  if (!el) return;
  
  el.className = `result-box ${isError ? 'error' : 'success'}`;
  
  if (typeof data === 'string') {
    el.textContent = data;
  } else {
    el.textContent = JSON.stringify(data, null, 2);
  }
}

// Utility: Parse hex input
function parseHex(hexStr) {
  return parseInt(hexStr.replace(/^0x/, ''), 16);
}

// Utility: Format byte as hex
function toHex(byte, width = 2) {
  return byte.toString(16).toUpperCase().padStart(width, '0');
}

// Helper: Ensure a base state exists and is selected
async function ensureBaseStateSelected() {
  // Check if a base state is already selected
  const bases = api?.gh?.getBaseStates ? await api.gh.getBaseStates() : { success: false };
  
  if (!bases?.success) return false;
  
  // If no base states exist, add one
  if (bases.baseStates?.length === 0) {
    await api.gh.addBaseState('TestBase');
  }
  
  // Get bases again
  const bases2 = await api.gh.getBaseStates();
  
  if (bases2?.success && bases2.baseStates?.length > 0) {
    // Select the first base state
    const baseId = bases2.baseStates[0].id;
    await api.gh.selectBaseState(baseId);
    return true;
  }
  return false;
}

// Helper: Ensure stockpile has entries by running full workflow
async function ensureStockpileHasEntries() {
  // Check stockpile first
  const stockpile = await api.gh.getStockpile();
  
  if (stockpile?.success && stockpile.stockpile?.length > 0) {
    return true; // Already have entries
  }
  
  // Ensure base state is selected
  await ensureBaseStateSelected();
  
  // Corrupt and stash
  const corruptResult = await api.gh.corruptAndStash();
  
  if (!corruptResult?.success) {
    return false;
  }
  
  // Get the stash entry
  const stash = await api.gh.getStash();
  
  if (!stash?.success || stash.stash?.length === 0) {
    return false;
  }
  
  // Promote to stockpile
  const stashId = stash.stash[0].id;
  const promoteResult = await api.gh.promoteStash(stashId);
  
  return promoteResult?.success === true;
}

// Test: Health Check
async function testHealth() {
  try {
    const data = await api.health();
    displayResult('healthResult', data);
  } catch (error) {
    displayResult('healthResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Domains
async function testDomains() {
  try {
    const data = await api.memory.getDomains();
    displayResult('domainsResult', data);
  } catch (error) {
    displayResult('domainsResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Domain Size
async function testDomainSize() {
  const domainName = document.getElementById('domainNameSize').value;
  try {
    const data = await api.memory.getDomainSize(domainName);
    displayResult('domainSizeResult', data);
  } catch (error) {
    displayResult('domainSizeResult', `Error: ${error.message}`, true);
  }
}

// Test: Peek Memory
async function testPeek() {
  const domain = document.getElementById('domainNamePeek').value;
  const addressHex = document.getElementById('addressPeek').value;
  const address = parseHex(addressHex);
  
  try {
    const data = await api.memory.peek(domain, address);
    
    if (data.success) {
      const formatted = {
        ...data,
        value: `0x${toHex(data.value)} (${data.value})`,
        address: `0x${toHex(data.address, 4)}`
      };
      displayResult('peekResult', formatted);
    } else {
      displayResult('peekResult', data, true);
    }
  } catch (error) {
    displayResult('peekResult', `Error: ${error.message}`, true);
  }
}

// Test: Poke Memory
async function testPoke() {
  const domain = document.getElementById('domainNamePoke').value;
  const addressHex = document.getElementById('addressPoke').value;
  const valueHex = document.getElementById('valuePoke').value;
  const address = parseHex(addressHex);
  const value = parseHex(valueHex);
  
  try {
    const data = await api.memory.poke(domain, address, value);
    
    if (data.success) {
      const formatted = {
        ...data,
        value: `0x${toHex(data.value)} (${data.value})`,
        address: `0x${toHex(data.address, 4)}`
      };
      displayResult('pokeResult', formatted);
    } else {
      displayResult('pokeResult', data, true);
    }
  } catch (error) {
    displayResult('pokeResult', `Error: ${error.message}`, true);
  }
}

// Test: Peek Range
async function testPeekRange() {
  const domain = document.getElementById('domainNamePeekRange').value;
  const addressHex = document.getElementById('addressPeekRange').value;
  const length = parseInt(document.getElementById('lengthPeekRange').value);
  const address = parseHex(addressHex);
  
  try {
    const data = await api.memory.peekRange(domain, address, length);
    
    if (data.success) {
      // Format data as hex dump
      let hexDump = `Address: 0x${toHex(address, 4)}, Length: ${length}\n`;
      hexDump += formatHexDump(data.data, address);
      
      const formatted = {
        success: true,
        domain: data.domain,
        hexDump: hexDump
      };
      displayResult('peekRangeResult', formatted);
    } else {
      displayResult('peekRangeResult', data, true);
    }
  } catch (error) {
    displayResult('peekRangeResult', `Error: ${error.message}`, true);
  }
}

// Test: Poke Range
async function testPokeRange() {
  const domain = document.getElementById('domainNamePokeRange').value;
  const addressHex = document.getElementById('addressPokeRange').value;
  const dataStr = document.getElementById('dataPokeRange').value;
  const address = parseHex(addressHex);
  
  // Parse hex bytes
  const dataBytes = dataStr.trim().split(/\s+/).map(b => parseHex(b));
  
  try {
    const data = await api.memory.pokeRange(domain, address, dataBytes);
    
    if (data.success) {
      const formatted = {
        ...data,
        address: `0x${toHex(data.address, 4)}`,
        writtenBytes: dataBytes.map(b => `0x${toHex(b)}`).join(' ')
      };
      displayResult('pokeRangeResult', formatted);
    } else {
      displayResult('pokeRangeResult', data, true);
    }
  } catch (error) {
    displayResult('pokeRangeResult', `Error: ${error.message}`, true);
  }
}

// Utility: Format hex dump
function formatHexDump(data, startAddress = 0) {
  let result = '';
  for (let i = 0; i < data.length; i += 16) {
    const addr = startAddress + i;
    const chunk = data.slice(i, i + 16);
    
    // Address
    result += toHex(addr, 4) + ':  ';
    
    // Hex values
    for (let j = 0; j < 16; j++) {
      if (j < chunk.length) {
        result += toHex(chunk[j]) + ' ';
      } else {
        result += '   ';
      }
      if (j === 7) result += ' ';
    }
    
    // ASCII
    result += ' |';
    for (let j = 0; j < chunk.length; j++) {
      const byte = chunk[j];
      result += (byte >= 32 && byte < 127) ? String.fromCharCode(byte) : '.';
    }
    result += '|\n';
  }
  return result;
}

// Test: Get CPU Registers
async function testCpuRegisters() {
  try {
    const data = await api.cpu.getRegisters();
    displayResult('cpuRegistersResult', data);
  } catch (error) {
    displayResult('cpuRegistersResult', `Error: ${error.message}`, true);
  }
}

// Test: Get CPU Core ID
async function testCpuCore() {
  try {
    const data = await api.cpu.getCore();
    displayResult('cpuCoreResult', data);
  } catch (error) {
    displayResult('cpuCoreResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Available CPU Cores
async function testCpuCores() {
  try {
    const data = await api.cpu.getCores();
    displayResult('cpuCoresResult', data);
  } catch (error) {
    displayResult('cpuCoresResult', `Error: ${error.message}`, true);
  }
}

// Test: Get CPU State Snapshot
async function testCpuState() {
  try {
    const data = await api.cpu.getState();
    displayResult('cpuStateResult', data);
  } catch (error) {
    displayResult('cpuStateResult', `Error: ${error.message}`, true);
  }
}

// Test: Get PPU Core ID
async function testPpuCore() {
  try {
    const data = await api.ppu.getCore();
    displayResult('ppuCoreResult', data);
  } catch (error) {
    displayResult('ppuCoreResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Available PPU Cores
async function testPpuCores() {
  try {
    const data = await api.ppu.getCores();
    displayResult('ppuCoresResult', data);
  } catch (error) {
    displayResult('ppuCoresResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Framebuffer (just info, not full data)
async function testPpuFramebuffer() {
  try {
    const data = await api.ppu.getFramebuffer();
    
    // Don't display full framebuffer data, just summary
    if (data.success) {
      const summary = {
        success: true,
        width: data.width,
        height: data.height,
        format: data.format,
        dataSize: data.data ? data.data.length : 0,
        dataSample: (data.data && Array.isArray(data.data)) 
          ? data.data.slice(0, 16).map(b => toHex(b)).join(' ') + '...' 
          : 'none'
      };
      displayResult('ppuFramebufferResult', summary);
    } else {
      displayResult('ppuFramebufferResult', data, true);
    }
  } catch (error) {
    displayResult('ppuFramebufferResult', `Error: ${error.message}`, true);
  }
}

// Test: Get PPU State Snapshot
async function testPpuState() {
  try {
    const data = await api.ppu.getState();
    displayResult('ppuStateResult', data);
  } catch (error) {
    displayResult('ppuStateResult', `Error: ${error.message}`, true);
  }
}

// Test: Get APU Core ID
async function testApuCore() {
  try {
    const data = await api.apu.getCore();
    displayResult('apuCoreResult', data);
  } catch (error) {
    displayResult('apuCoreResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Available APU Cores
async function testApuCores() {
  try {
    const data = await api.apu.getCores();
    displayResult('apuCoresResult', data);
  } catch (error) {
    displayResult('apuCoresResult', `Error: ${error.message}`, true);
  }
}

// Test: Get APU Channels State
async function testApuChannels() {
  try {
    const data = await api.apu.getChannels();
    displayResult('apuChannelsResult', data);
  } catch (error) {
    displayResult('apuChannelsResult', `Error: ${error.message}`, true);
  }
}

// Test: Set CPU Registers
async function testSetCpuRegisters() {
  try {
    const data = await api.cpu.setRegisters({
      PC: 0x8000,
      A: 0xFF,
      X: 0x10,
      Y: 0x20,
      P: 0x24,
      SP: 0xFD
    });
    displayResult('setCpuRegistersResult', data);
  } catch (error) {
    displayResult('setCpuRegistersResult', `Error: ${error.message}`, true);
  }
}

// Test: Get OAM Data
async function testOamData() {
  try {
    const data = await api.ppu.getOam();
    
    if (data.success) {
      const summary = {
        success: true,
        size: data.size,
        spriteCount: data.spriteCount,
        dataSample: (data.data && Array.isArray(data.data)) 
          ? data.data.slice(0, 16).map(b => toHex(b)).join(' ') + '...' 
          : 'none'
      };
      displayResult('oamDataResult', summary);
    } else {
      displayResult('oamDataResult', data, true);
    }
  } catch (error) {
    displayResult('oamDataResult', `Error: ${error.message}`, true);
  }
}

// Test: Get RTC Domains
async function testRtcDomains() {
  try {
    const data = await api.rtc.getDomains();
    displayResult('rtcDomainsResult', data);
  } catch (error) {
    displayResult('rtcDomainsResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Corruption Intensity
async function testRtcIntensity() {
  try {
    const data = await api.rtc.getIntensity();
    displayResult('rtcIntensityResult', data);
  } catch (error) {
    displayResult('rtcIntensityResult', `Error: ${error.message}`, true);
  }
}

// Test: Set Corruption Intensity
async function testRtcSetIntensity() {
  try {
    const data = await api.rtc.setIntensity(5);
    displayResult('rtcSetIntensityResult', data);
  } catch (error) {
    displayResult('rtcSetIntensityResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Blast Type
async function testRtcBlastType() {
  try {
    const data = await api.rtc.getBlastType();
    displayResult('rtcBlastTypeResult', data);
  } catch (error) {
    displayResult('rtcBlastTypeResult', `Error: ${error.message}`, true);
  }
}

// Test: Set Blast Type
async function testRtcSetBlastType() {
  try {
    const data = await api.rtc.setBlastType('TILT');
    displayResult('rtcSetBlastTypeResult', data);
  } catch (error) {
    displayResult('rtcSetBlastTypeResult', `Error: ${error.message}`, true);
  }
}

// Test: Execute Blast
async function testRtcBlast() {
  try {
    const data = await api.rtc.blast();
    displayResult('rtcBlastResult', data);
  } catch (error) {
    displayResult('rtcBlastResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Auto-Corrupt State
async function testRtcAutoCorrupt() {
  try {
    const data = await api.rtc.getAutoCorrupt();
    displayResult('rtcAutoCorruptResult', data);
  } catch (error) {
    displayResult('rtcAutoCorruptResult', `Error: ${error.message}`, true);
  }
}

// Test: Toggle Auto-Corrupt
async function testRtcToggleAutoCorrupt() {
  try {
    const data = await api.rtc.setAutoCorrupt(true);
    displayResult('rtcToggleAutoCorruptResult', data);
  } catch (error) {
    displayResult('rtcToggleAutoCorruptResult', `Error: ${error.message}`, true);
  }
}

// Test: Let It Rip
async function testRtcLetItRip() {
  try {
    const data = await api.rtc.letItRip();
    displayResult('rtcLetItRipResult', data);
  } catch (error) {
    displayResult('rtcLetItRipResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Crash Behavior
async function testRtcCrashBehavior() {
  try {
    const data = await api.rtc.getCrashBehavior();
    displayResult('rtcCrashBehaviorResult', data);
  } catch (error) {
    displayResult('rtcCrashBehaviorResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Stubborn Mode
async function testRtcStubbornMode() {
  try {
    const data = await api.rtc.getStubbornMode();
    displayResult('rtcStubbornModeResult', data);
  } catch (error) {
    displayResult('rtcStubbornModeResult', `Error: ${error.message}`, true);
  }
}

// Test: Set Stubborn Mode
async function testRtcSetStubbornMode() {
  try {
    const data = await api.rtc.setStubbornMode(true);
    displayResult('rtcSetStubbornModeResult', data);
  } catch (error) {
    displayResult('rtcSetStubbornModeResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Last Blast Info
async function testRtcLastBlast() {
  try {
    const data = await api.rtc.getLastBlast();
    displayResult('rtcLastBlastResult', data);
  } catch (error) {
    displayResult('rtcLastBlastResult', `Error: ${error.message}`, true);
  }
}

// Run all tests sequentially
async function runAllTests() {
  const progressContainer = document.getElementById('progressContainer');
  const progressBar = document.getElementById('progressBar');
  const progressPercent = document.getElementById('progressPercent');
  const progressStatus = document.getElementById('progressStatus');
  const passCountEl = document.getElementById('passCount');
  const failCountEl = document.getElementById('failCount');
  
  // Show progress container
  progressContainer.style.display = 'block';
  progressBar.style.width = '0%';
  progressPercent.textContent = '0';
  
  const tests = [
    { name: 'Health Check', fn: testHealth, resultId: 'healthResult' },
    { name: 'Get Domains', fn: testDomains, resultId: 'domainsResult' },
    { name: 'Get Domain Size', fn: testDomainSize, resultId: 'domainSizeResult' },
    { name: 'Peek Memory', fn: testPeek, resultId: 'peekResult' },
    { name: 'Poke Memory', fn: testPoke, resultId: 'pokeResult' },
    { name: 'Peek Range', fn: testPeekRange, resultId: 'peekRangeResult' },
    { name: 'Poke Range', fn: testPokeRange, resultId: 'pokeRangeResult' },
    { name: 'CPU Registers', fn: testCpuRegisters, resultId: 'cpuRegistersResult' },
    { name: 'CPU Core ID', fn: testCpuCore, resultId: 'cpuCoreResult' },
    { name: 'CPU Cores List', fn: testCpuCores, resultId: 'cpuCoresResult' },
    { name: 'CPU State', fn: testCpuState, resultId: 'cpuStateResult' },
    { name: 'PPU Core ID', fn: testPpuCore, resultId: 'ppuCoreResult' },
    { name: 'PPU Cores List', fn: testPpuCores, resultId: 'ppuCoresResult' },
    { name: 'PPU Framebuffer', fn: testPpuFramebuffer, resultId: 'ppuFramebufferResult' },
    { name: 'PPU State', fn: testPpuState, resultId: 'ppuStateResult' },
    { name: 'APU Core ID', fn: testApuCore, resultId: 'apuCoreResult' },
    { name: 'APU Cores List', fn: testApuCores, resultId: 'apuCoresResult' },
    { name: 'APU Channels', fn: testApuChannels, resultId: 'apuChannelsResult' },
    { name: 'Set CPU Registers', fn: testSetCpuRegisters, resultId: 'setCpuRegistersResult' },
    { name: 'Get OAM Data', fn: testOamData, resultId: 'oamDataResult' },
    { name: 'RTC Domains', fn: testRtcDomains, resultId: 'rtcDomainsResult' },
    { name: 'RTC Intensity', fn: testRtcIntensity, resultId: 'rtcIntensityResult' },
    { name: 'RTC Set Intensity', fn: testRtcSetIntensity, resultId: 'rtcSetIntensityResult' },
    { name: 'RTC Blast Type', fn: testRtcBlastType, resultId: 'rtcBlastTypeResult' },
    { name: 'RTC Set Blast Type', fn: testRtcSetBlastType, resultId: 'rtcSetBlastTypeResult' },
    { name: 'RTC Blast', fn: testRtcBlast, resultId: 'rtcBlastResult' },
    { name: 'RTC Auto-Corrupt', fn: testRtcAutoCorrupt, resultId: 'rtcAutoCorruptResult' },
    { name: 'RTC Toggle Auto-Corrupt', fn: testRtcToggleAutoCorrupt, resultId: 'rtcToggleAutoCorruptResult' },
    { name: 'RTC Let It Rip', fn: testRtcLetItRip, resultId: 'rtcLetItRipResult' },
    { name: 'RTC Crash Behavior', fn: testRtcCrashBehavior, resultId: 'rtcCrashBehaviorResult' },
    { name: 'RTC Stubborn Mode', fn: testRtcStubbornMode, resultId: 'rtcStubbornModeResult' },
    { name: 'RTC Set Stubborn Mode', fn: testRtcSetStubbornMode, resultId: 'rtcSetStubbornModeResult' },
    { name: 'RTC Last Blast', fn: testRtcLastBlast, resultId: 'rtcLastBlastResult' },
    { name: 'GH Get Base States', fn: testGhGetBases, resultId: 'ghGetBasesResult' },
    { name: 'GH Add Base State', fn: testGhAddBase, resultId: 'ghAddBaseResult' },
    { name: 'GH Select Base', fn: testGhSelectBase, resultId: 'ghSelectBaseResult' },
    { name: 'GH Load Base', fn: testGhLoadBase, resultId: 'ghLoadBaseResult' },
    { name: 'GH Load On Operation', fn: testGhLoadOnOperation, resultId: 'ghLoadOnOperationResult' },
    { name: 'GH Corrupt & Stash', fn: testGhCorruptAndStash, resultId: 'ghCorruptAndStashResult' },
    { name: 'GH Get Stash', fn: testGhGetStash, resultId: 'ghGetStashResult' },
    { name: 'GH Replay Stash', fn: testGhReplayStash, resultId: 'ghReplayStashResult' },
    { name: 'GH Promote Stash', fn: testGhPromoteStash, resultId: 'ghPromoteStashResult' },
    { name: 'GH Get Stockpile', fn: testGhGetStockpile, resultId: 'ghGetStockpileResult' },
    { name: 'GH Replay Stockpile', fn: testGhReplayStockpile, resultId: 'ghReplayStockpileResult' },
    { name: 'GH Rename Stockpile', fn: testGhRenameStockpile, resultId: 'ghRenameStockpileResult' },
    { name: 'GH Export Stockpile', fn: testGhExportStockpile, resultId: 'ghExportStockpileResult' },
    { name: 'GH Delete Stockpile', fn: testGhDeleteStockpile, resultId: 'ghDeleteStockpileResult' },
    { name: 'GH Delete Stash', fn: testGhDeleteStash, resultId: 'ghDeleteStashResult' },
    { name: 'GH Clear Stash', fn: testGhClearStash, resultId: 'ghClearStashResult' },
    { name: 'GH Import Stockpile', fn: testGhImportStockpile, resultId: 'ghImportStockpileResult' },
    { name: 'GH Delete Base', fn: testGhDeleteBase, resultId: 'ghDeleteBaseResult' },
    { name: 'GH Full Workflow (ID-based)', fn: testGhFullWorkflow, resultId: 'ghFullWorkflowResult' },
    { name: 'Imagine Model Loaded', fn: testImagineModelLoaded, resultId: 'imagineModelLoadedResult' },
    { name: 'Imagine Get Epoch', fn: testImagineGetEpoch, resultId: 'imagineGetEpochResult' },
    { name: 'Imagine Set Epoch', fn: testImagineSetEpoch, resultId: 'imagineSetEpochResult' },
    { name: 'Imagine Load Model', fn: testImagineLoadModel, resultId: 'imagineLoadModelResult' },
    { name: 'Imagine Get Params', fn: testImagineGetParams, resultId: 'imagineGetParamsResult' },
    { name: 'Imagine Set Params', fn: testImagineSetParams, resultId: 'imagineSetParamsResult' },
    { name: 'Imagine Freeze & Fetch', fn: testImagineFreezeAndFetch, resultId: 'imagineFreezeAndFetchResult' },
    { name: 'Imagine Get Snapshot', fn: testImagineGetSnapshot, resultId: 'imagineGetSnapshotResult' },
    { name: 'Imagine Run Prediction', fn: testImagineRunPrediction, resultId: 'imagineRunPredictionResult' },
    { name: 'Imagine Apply Patch', fn: testImagineApplyPatch, resultId: 'imagineApplyPatchResult' },
    { name: 'Imagine a Bug', fn: testImagineABug, resultId: 'imagineABugResult' },
    { name: 'Imagine Get Predicted Bytes', fn: testImagineGetPredictedBytes, resultId: 'imagineGetPredictedBytesResult' },
    { name: 'Imagine Get Last Error', fn: testImagineGetLastError, resultId: 'imagineGetLastErrorResult' },
    { name: 'Achievement Init', fn: testAchInit, resultId: 'achInitResult' },
    { name: 'Achievement Get List', fn: testAchGetList, resultId: 'achGetListResult' },
    { name: 'Achievement Get State', fn: testAchGetState, resultId: 'achGetStateResult' },
    { name: 'Achievement Get Progress', fn: testAchGetProgress, resultId: 'achGetProgressResult' },
    { name: 'Achievement Get Conditions', fn: testAchGetConditions, resultId: 'achGetConditionsResult' },
    { name: 'Achievement Force Complete', fn: testAchForceComplete, resultId: 'achForceCompleteResult' },
    { name: 'Achievement Evaluate Frame', fn: testAchEvaluate, resultId: 'achEvaluateResult' }
  ];
  
  let passCount = 0;
  let failCount = 0;
  
  for (let i = 0; i < tests.length; i++) {
    const test = tests[i];
    const progress = Math.round(((i + 1) / tests.length) * 100);
    
    // Update status
    progressStatus.textContent = `Running: ${test.name}...`;
    
    // Run the test
    await test.fn();
    
    // Check the result box to see if test passed or failed
    const resultBox = document.getElementById(test.resultId);
    if (resultBox && resultBox.className.includes('success')) {
      passCount++;
      passCountEl.textContent = passCount;
    } else if (resultBox && resultBox.className.includes('error')) {
      failCount++;
      failCountEl.textContent = failCount;
    }
    
    // Update progress bar
    progressBar.style.width = `${progress}%`;
    progressPercent.textContent = progress;
    
    // Small delay to see each test
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  
  // Final status
  if (failCount === 0) {
    progressStatus.textContent = `✅ All tests passed! (${passCount}/${tests.length})`;
    progressStatus.style.color = '#00ff00';
  } else {
    progressStatus.textContent = `⚠️ Tests completed: ${passCount} passed, ${failCount} failed`;
    progressStatus.style.color = '#ff6666';
  }
}

// Event listeners
document.addEventListener('DOMContentLoaded', () => {
  document.getElementById('btnHealth')?.addEventListener('click', testHealth);
  document.getElementById('btnDomains')?.addEventListener('click', testDomains);
  document.getElementById('btnDomainSize')?.addEventListener('click', testDomainSize);
  document.getElementById('btnPeek')?.addEventListener('click', testPeek);
  document.getElementById('btnPoke')?.addEventListener('click', testPoke);
  document.getElementById('btnPeekRange')?.addEventListener('click', testPeekRange);
  document.getElementById('btnPokeRange')?.addEventListener('click', testPokeRange);
  document.getElementById('btnRunAll')?.addEventListener('click', runAllTests);
  
  // CPU State tests
  document.getElementById('btnCpuRegisters')?.addEventListener('click', testCpuRegisters);
  document.getElementById('btnCpuCore')?.addEventListener('click', testCpuCore);
  document.getElementById('btnCpuCores')?.addEventListener('click', testCpuCores);
  document.getElementById('btnCpuState')?.addEventListener('click', testCpuState);
  
  // PPU State tests
  document.getElementById('btnPpuCore')?.addEventListener('click', testPpuCore);
  document.getElementById('btnPpuCores')?.addEventListener('click', testPpuCores);
  document.getElementById('btnPpuFramebuffer')?.addEventListener('click', testPpuFramebuffer);
  document.getElementById('btnPpuState')?.addEventListener('click', testPpuState);
  
  // APU State tests
  document.getElementById('btnApuCore')?.addEventListener('click', testApuCore);
  document.getElementById('btnApuCores')?.addEventListener('click', testApuCores);
  document.getElementById('btnApuChannels')?.addEventListener('click', testApuChannels);
  
  // CPU Register Manipulation
  document.getElementById('btnSetCpuRegisters')?.addEventListener('click', testSetCpuRegisters);
  
  // PPU OAM Data
  document.getElementById('btnOamData')?.addEventListener('click', testOamData);
  
  // RTC tests
  document.getElementById('btnRtcDomains')?.addEventListener('click', testRtcDomains);
  document.getElementById('btnRtcIntensity')?.addEventListener('click', testRtcIntensity);
  document.getElementById('btnRtcSetIntensity')?.addEventListener('click', testRtcSetIntensity);
  document.getElementById('btnRtcBlastType')?.addEventListener('click', testRtcBlastType);
  document.getElementById('btnRtcSetBlastType')?.addEventListener('click', testRtcSetBlastType);
  document.getElementById('btnRtcBlast')?.addEventListener('click', testRtcBlast);
  document.getElementById('btnRtcAutoCorrupt')?.addEventListener('click', testRtcAutoCorrupt);
  document.getElementById('btnRtcToggleAutoCorrupt')?.addEventListener('click', testRtcToggleAutoCorrupt);
  document.getElementById('btnRtcLetItRip')?.addEventListener('click', testRtcLetItRip);
  document.getElementById('btnRtcCrashBehavior')?.addEventListener('click', testRtcCrashBehavior);
  document.getElementById('btnRtcStubbornMode')?.addEventListener('click', testRtcStubbornMode);
  document.getElementById('btnRtcSetStubbornMode')?.addEventListener('click', testRtcSetStubbornMode);
  document.getElementById('btnRtcLastBlast')?.addEventListener('click', testRtcLastBlast);
  
  // Auto-run health check on load
  testHealth();
});

// ==================================================
// Glitch Harvester Tests
// ==================================================

async function testGhGetBases() {
  try {
    const data = await api.gh.getBaseStates();
    if (data?.success) {
      const bases = data.baseStates || [];
      displayResult('ghGetBasesResult', { 
        selectedId: data.selectedId,
        count: bases.length, 
        bases: bases.slice(0, 5)
      });
    } else {
      displayResult('ghGetBasesResult', data);
    }
  } catch (error) {
    displayResult('ghGetBasesResult', `Error: ${error.message}`, true);
  }
}

async function testGhAddBase() {
  try {
    const data = await api.gh.addBaseState('Test Base ' + Date.now());
    displayResult('ghAddBaseResult', data);
  } catch (error) {
    displayResult('ghAddBaseResult', `Error: ${error.message}`, true);
  }
}

async function testGhSelectBase() {
  try {
    // First get the bases to find an ID
    const bases = await api.gh.getBaseStates();
    
    if (bases?.success && bases.baseStates?.length > 0) {
      const firstId = bases.baseStates[0].id;
      const data = await api.gh.selectBase(firstId);
      displayResult('ghSelectBaseResult', data);
    } else {
      displayResult('ghSelectBaseResult', { error: 'No base states available. Add one first.' }, true);
    }
  } catch (error) {
    displayResult('ghSelectBaseResult', `Error: ${error.message}`, true);
  }
}

async function testGhLoadBase() {
  try {
    const data = await api.gh.loadBase();
    displayResult('ghLoadBaseResult', data);
  } catch (error) {
    displayResult('ghLoadBaseResult', `Error: ${error.message}`, true);
  }
}

async function testGhDeleteBase() {
  try {
    // First get the bases to find an ID
    const bases = await api.gh.getBaseStates();
    
    if (bases?.success && bases.baseStates?.length > 0) {
      const firstId = bases.baseStates[0].id;
      const data = await api.gh.deleteBaseState(firstId);
      displayResult('ghDeleteBaseResult', data);
    } else {
      displayResult('ghDeleteBaseResult', { error: 'No base states available to delete.' }, true);
    }
  } catch (error) {
    displayResult('ghDeleteBaseResult', `Error: ${error.message}`, true);
  }
}

async function testGhLoadOnOperation() {
  try {
    // Get current value
    const current = await api.gh.getLoadOnOperation();
    
    if (current?.success) {
      // Toggle it
      const newValue = !current.loadOnOperation;
      const data = await api.gh.setLoadOnOperation(newValue);
      displayResult('ghLoadOnOperationResult', { 
        previous: current.loadOnOperation, 
        new: data.loadOnOperation 
      });
    } else {
      displayResult('ghLoadOnOperationResult', current);
    }
  } catch (error) {
    displayResult('ghLoadOnOperationResult', `Error: ${error.message}`, true);
  }
}

async function testGhCorruptAndStash() {
  try {
    const data = await api.gh.corruptAndStash();
    displayResult('ghCorruptAndStashResult', data);
  } catch (error) {
    displayResult('ghCorruptAndStashResult', `Error: ${error.message}`, true);
  }
}

async function testGhGetStash() {
  try {
    const data = await api.gh.getStash();
    if (data?.success) {
      const stash = data.stash || [];
      displayResult('ghGetStashResult', { 
        count: stash.length, 
        entries: stash.slice(0, 5) 
      });
    } else {
      displayResult('ghGetStashResult', data);
    }
  } catch (error) {
    displayResult('ghGetStashResult', `Error: ${error.message}`, true);
  }
}

async function testGhReplayStash() {
  try {
    // First get the stash to find an ID
    const stash = await api.gh.getStash();
    
    if (stash?.success && stash.stash?.length > 0) {
      const firstId = stash.stash[0].id;
      const data = await api.gh.replayStash(firstId);
      displayResult('ghReplayStashResult', data);
    } else {
      displayResult('ghReplayStashResult', { error: 'No stash entries available. Corrupt and stash first.' }, true);
    }
  } catch (error) {
    displayResult('ghReplayStashResult', `Error: ${error.message}`, true);
  }
}

async function testGhPromoteStash() {
  try {
    // First get the stash to find an ID
    let stash = await api.gh.getStash();
    
    // If stash is empty, create an entry
    if (!stash?.success || stash.stash?.length === 0) {
      // Ensure base state is selected first
      await ensureBaseStateSelected();
      
      await api.gh.corruptAndStash();
      
      // Refresh stash
      stash = await api.gh.getStash();
    }
    
    if (stash?.success && stash.stash?.length > 0) {
      const firstId = stash.stash[0].id;
      const data = await api.gh.promoteStash(firstId);
      displayResult('ghPromoteStashResult', data);
    } else {
      displayResult('ghPromoteStashResult', { error: 'Failed to create stash entry for promotion.' }, true);
    }
  } catch (error) {
    displayResult('ghPromoteStashResult', `Error: ${error.message}`, true);
  }
}

async function testGhDeleteStash() {
  try {
    // First get the stash to find an ID
    const stash = await api.gh.getStash();
    
    if (stash?.success && stash.stash?.length > 0) {
      const firstId = stash.stash[0].id;
      const data = await api.gh.deleteStash(firstId);
      displayResult('ghDeleteStashResult', data);
    } else {
      displayResult('ghDeleteStashResult', { error: 'No stash entries available to delete.' }, true);
    }
  } catch (error) {
    displayResult('ghDeleteStashResult', `Error: ${error.message}`, true);
  }
}

async function testGhClearStash() {
  try {
    const data = await api.gh.clearStash();
    displayResult('ghClearStashResult', data);
  } catch (error) {
    displayResult('ghClearStashResult', `Error: ${error.message}`, true);
  }
}

async function testGhGetStockpile() {
  try {
    // Ensure stockpile has entries (creates base state, corrupts, promotes if needed)
    await ensureStockpileHasEntries();
    
    const data = await api.gh.getStockpile();
    
    if (data?.success) {
      const stockpile = data.stockpile || [];
      displayResult('ghGetStockpileResult', { 
        count: stockpile.length, 
        entries: stockpile.slice(0, 5) 
      });
    } else {
      displayResult('ghGetStockpileResult', data);
    }
  } catch (error) {
    displayResult('ghGetStockpileResult', `Error: ${error.message}`, true);
  }
}

async function testGhReplayStockpile() {
  try {
    // Ensure stockpile has entries (creates base state, corrupts, promotes if needed)
    await ensureStockpileHasEntries();
    
    // Get the stockpile
    const stockpile = await api.gh.getStockpile();
    
    if (stockpile?.success && stockpile.stockpile?.length > 0) {
      const firstId = stockpile.stockpile[0].id;
      const data = await api.gh.replayStockpile(firstId);
      displayResult('ghReplayStockpileResult', data);
    } else {
      displayResult('ghReplayStockpileResult', { error: 'Failed to create stockpile entry for test.' }, true);
    }
  } catch (error) {
    displayResult('ghReplayStockpileResult', `Error: ${error.message}`, true);
  }
}

async function testGhRenameStockpile() {
  try {
    // Ensure stockpile has entries (creates base state, corrupts, promotes if needed)
    await ensureStockpileHasEntries();
    
    // Get the stockpile
    const stockpile = await api.gh.getStockpile();
    
    if (stockpile?.success && stockpile.stockpile?.length > 0) {
      const firstId = stockpile.stockpile[0].id;
      const data = await api.gh.renameStockpile(firstId, 'Renamed Entry ' + Date.now());
      displayResult('ghRenameStockpileResult', data);
    } else {
      displayResult('ghRenameStockpileResult', { error: 'Failed to create stockpile entry for test.' }, true);
    }
  } catch (error) {
    displayResult('ghRenameStockpileResult', `Error: ${error.message}`, true);
  }
}

async function testGhDeleteStockpile() {
  try {
    // Ensure stockpile has entries (creates base state, corrupts, promotes if needed)
    await ensureStockpileHasEntries();
    
    // Get the stockpile
    const stockpile = await api.gh.getStockpile();
    
    if (stockpile?.success && stockpile.stockpile?.length > 0) {
      const firstId = stockpile.stockpile[0].id;
      const data = await api.gh.deleteStockpile(firstId);
      displayResult('ghDeleteStockpileResult', data);
    } else {
      displayResult('ghDeleteStockpileResult', { error: 'Failed to create stockpile entry for test.' }, true);
    }
  } catch (error) {
    displayResult('ghDeleteStockpileResult', `Error: ${error.message}`, true);
  }
}

async function testGhExportStockpile() {
  try {
    const data = await api.gh.exportStockpile();
    if (data?.success) {
      displayResult('ghExportStockpileResult', { 
        success: true, 
        jsonLength: data.json?.length || 0,
        preview: data.json?.substring(0, 200) + '...'
      });
    } else {
      displayResult('ghExportStockpileResult', data);
    }
  } catch (error) {
    displayResult('ghExportStockpileResult', `Error: ${error.message}`, true);
  }
}

async function testGhImportStockpile() {
  try {
    // Export first to get valid JSON
    const exportResult = await api.gh.exportStockpile();
    
    if (exportResult?.success && exportResult.json) {
      const data = await api.gh.importStockpile(exportResult.json);
      displayResult('ghImportStockpileResult', data);
    } else {
      displayResult('ghImportStockpileResult', { error: 'No stockpile to export/import. Create some entries first.' }, true);
    }
  } catch (error) {
    displayResult('ghImportStockpileResult', `Error: ${error.message}`, true);
  }
}

// ==============================================================================
// COMPREHENSIVE TEST: Full Glitch Harvester ID-Based Workflow
// This test validates the complete web UI workflow as it would be implemented:
// 1. Query lists to populate UI (get IDs)
// 2. User selects items from UI (extract specific IDs)
// 3. User performs actions (use those IDs)
// This is the proper state machine approach for a web module implementation.
// ==============================================================================
async function testGhFullWorkflow() {
  const log = [];
  try {
    log.push('=== GLITCH HARVESTER FULL ID-BASED WORKFLOW TEST ===\n');
    
    // STEP 1: Setup - Ensure we have a base state (simulates initial app state)
    log.push('STEP 1: Query base states list (populate base state dropdown)');
    let basesData = await api.gh.getBaseStates();
    
    if (!basesData.success || basesData.baseStates.length === 0) {
      log.push('  → No base states found, creating one...');
      await api.gh.addBaseState('WebUI Test Base');
      basesData = await api.gh.getBaseStates();
    }
    
    // Extract ID from list (simulates user selecting from dropdown)
    const baseStateId = basesData.baseStates[0].id;
    log.push(`  ✓ Base states listed: ${basesData.baseStates.length} items`);
    log.push(`  → User selects base: ID="${baseStateId}"\n`);
    
    // STEP 2: Select the base state (simulates clicking "Select" button)
    log.push('STEP 2: Select base state by ID (user clicks "Use This Base")');
    const selectData = await api.gh.selectBaseState(baseStateId);
    log.push(`  ✓ Base state selected: ${selectData.success}\n`);
    
    // STEP 3: Corrupt and stash (simulates clicking "Corrupt & Add to Stash")
    log.push('STEP 3: Generate corruption (user clicks "Corrupt & Stash")');
    const corruptData = await api.gh.corruptAndStash();
    const createdStashId = corruptData.entry?.id;
    log.push(`  ✓ Corruption created: ${corruptData.success}`);
    log.push(`  → New stash entry ID: "${createdStashId}"\n`);
    
    // STEP 4: Query stash list (simulates refreshing stash history UI list)
    log.push('STEP 4: Query stash list (populate stash history list in UI)');
    const stashData = await api.gh.getStash();
    log.push(`  ✓ Stash entries listed: ${stashData.stash.length} items`);
    
    // Find our created entry in the list (simulates user scrolling/finding the item)
    const stashItem = stashData.stash.find(e => e.id === createdStashId);
    log.push(`  → User finds entry: "${stashItem.name}" (ID="${stashItem.id}")\n`);
    
    // STEP 5: Replay stash entry (simulates selecting item and clicking "Replay")
    log.push('STEP 5: Replay stash entry by ID (user selects item, clicks "Replay")');
    const replayStashData = await api.gh.replayStash(stashItem.id);
    log.push(`  ✓ Stash replay executed: ${replayStashData.success}\n`);
    
    // STEP 6: Promote to stockpile (simulates selecting item and clicking "Keep")
    log.push('STEP 6: Promote stash to stockpile by ID (user clicks "Keep")');
    const promoteData = await api.gh.promoteStash(stashItem.id);
    const promotedStockpileId = promoteData.entry?.id;
    log.push(`  ✓ Promoted to stockpile: ${promoteData.success}`);
    log.push(`  → Stockpile entry ID: "${promotedStockpileId}"\n`);
    
    // STEP 7: Query stockpile list (simulates refreshing stockpile UI list)
    log.push('STEP 7: Query stockpile list (populate stockpile list in UI)');
    const stockpileData = await api.gh.getStockpile();
    log.push(`  ✓ Stockpile entries listed: ${stockpileData.stockpile.length} items`);
    
    // Find our promoted entry (simulates user finding it in the UI list)
    const stockpileItem = stockpileData.stockpile.find(e => e.id === promotedStockpileId);
    log.push(`  → User finds entry: "${stockpileItem.name}" (ID="${stockpileItem.id}")\n`);
    
    // STEP 8: Replay stockpile entry (simulates selecting and clicking "Replay")
    log.push('STEP 8: Replay stockpile entry by ID (user selects, clicks "Replay")');
    const replayStockData = await api.gh.replayStockpile(stockpileItem.id);
    log.push(`  ✓ Stockpile replay executed: ${replayStockData.success}\n`);
    
    // STEP 9: Rename stockpile entry (simulates editing name in UI)
    log.push('STEP 9: Rename stockpile entry by ID (user edits name)');
    const newName = `Favorite Glitch ${Date.now()}`;
    const renameData = await api.gh.renameStockpile(stockpileItem.id, newName);
    log.push(`  ✓ Renamed: ${renameData.success}`);
    log.push(`  → New name: "${newName}"\n`);
    
    // STEP 10: Export stockpile (simulates clicking "Export" button)
    log.push('STEP 10: Export stockpile (user clicks "Export")');
    const exportData = await api.gh.exportStockpile();
    log.push(`  ✓ Export created: ${exportData.success}`);
    log.push(`  → JSON length: ${exportData.json?.length || 0} bytes\n`);
    
    // STEP 11: Delete stockpile entry (simulates selecting and clicking "Delete")
    log.push('STEP 11: Delete stockpile entry by ID (user clicks "Delete")');
    const deleteData = await api.gh.deleteStockpile(stockpileItem.id);
    log.push(`  ✓ Deleted: ${deleteData.success}\n`);
    
    // STEP 12: Verify deletion (simulates UI refresh)
    log.push('STEP 12: Verify deletion (UI refreshes list)');
    const verifyData = await api.gh.getStockpile();
    const stillExists = verifyData.stockpile.some(e => e.id === stockpileItem.id);
    log.push(`  ✓ Entry removed from list: ${!stillExists}\n`);
    
    log.push('=== WORKFLOW TEST COMPLETE ===');
    log.push('All ID-based state transitions validated successfully!');
    log.push('This workflow is suitable for web UI implementation.');
    
    displayResult('ghFullWorkflowResult', log.join('\n'));
  } catch (error) {
    log.push(`\n❌ ERROR: ${error.message}`);
    displayResult('ghFullWorkflowResult', log.join('\n'), true);
  }
}

// ==============================================================================
// IMAGINE (AI-Powered Corruption) TESTS
// ==============================================================================

// Test: Get Model Loaded State
async function testImagineModelLoaded() {
  try {
    const data = await api.imagine.isModelLoaded();
    displayResult('imagineModelLoadedResult', data);
  } catch (error) {
    displayResult('imagineModelLoadedResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Current Epoch
async function testImagineGetEpoch() {
  try {
    const data = await api.imagine.getEpoch();
    displayResult('imagineGetEpochResult', data);
  } catch (error) {
    displayResult('imagineGetEpochResult', `Error: ${error.message}`, true);
  }
}

// Test: Set Epoch
async function testImagineSetEpoch() {
  const epoch = parseInt(document.getElementById('imagineEpoch')?.value || '30');
  try {
    const data = await api.imagine.setEpoch(epoch);
    displayResult('imagineSetEpochResult', data);
  } catch (error) {
    displayResult('imagineSetEpochResult', `Error: ${error.message}`, true);
  }
}

// Test: Load Model
async function testImagineLoadModel() {
  const epoch = parseInt(document.getElementById('imagineLoadEpoch')?.value || '30');
  try {
    const data = await api.imagine.loadModel(epoch);
    displayResult('imagineLoadModelResult', data);
  } catch (error) {
    displayResult('imagineLoadModelResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Generation Parameters
async function testImagineGetParams() {
  try {
    const data = await api.imagine.getGenerationParams();
    displayResult('imagineGetParamsResult', data);
  } catch (error) {
    displayResult('imagineGetParamsResult', `Error: ${error.message}`, true);
  }
}

// Test: Set Generation Parameters
async function testImagineSetParams() {
  const bytesToGenerate = parseInt(document.getElementById('imagineBytesToGenerate')?.value || '2');
  const temperature = parseFloat(document.getElementById('imagineTemperature')?.value || '0.4');
  const topK = parseInt(document.getElementById('imagineTopK')?.value || '1');
  
  try {
    const data = await api.imagine.setGenerationParams({
      bytesToGenerate,
      temperature,
      topK
    });
    displayResult('imagineSetParamsResult', data);
  } catch (error) {
    displayResult('imagineSetParamsResult', `Error: ${error.message}`, true);
  }
}

// Test: Freeze and Fetch Next Instruction (Capture CPU Snapshot)
async function testImagineFreezeAndFetch() {
  try {
    const data = await api.imagine.freezeAndFetch();
    
    // Format snapshot for better display
    if (data.success && data.snapshot) {
      const prev8Array = Array.isArray(data.snapshot.prev8) ? data.snapshot.prev8 : (data.snapshot.prev8 ? Object.values(data.snapshot.prev8) : []);
      const next16Array = Array.isArray(data.snapshot.next16) ? data.snapshot.next16 : (data.snapshot.next16 ? Object.values(data.snapshot.next16) : []);
      
      const formatted = {
        success: data.success,
        message: data.message,
        snapshot: {
          cpuCoreId: data.snapshot.cpuCoreId,
          pc: `0x${toHex(data.snapshot.pc, 4)}`,
          registers: {
            a: `0x${toHex(data.snapshot.a)}`,
            x: `0x${toHex(data.snapshot.x)}`,
            y: `0x${toHex(data.snapshot.y)}`,
            p: `0x${toHex(data.snapshot.p)}`,
            sp: `0x${toHex(data.snapshot.sp, 4)}`
          },
          flags: {
            irq: data.snapshot.irq,
            nmi: data.snapshot.nmi,
            inPrgRom: data.snapshot.inPrgRom
          },
          prev8: prev8Array.length > 0 ? prev8Array.map(b => `0x${toHex(b)}`).join(' ') : 'N/A',
          next16: next16Array.length > 0 ? next16Array.slice(0, 8).map(b => `0x${toHex(b)}`).join(' ') + '...' : 'N/A'
        }
      };
      displayResult('imagineFreezeAndFetchResult', formatted);
    } else {
      displayResult('imagineFreezeAndFetchResult', data);
    }
  } catch (error) {
    displayResult('imagineFreezeAndFetchResult', `Error: ${error.message}`, true);
  }
}

// Test: Get CPU Snapshot
async function testImagineGetSnapshot() {
  try {
    const data = await api.imagine.getCpuSnapshot();
    
    // Format snapshot for better display
    if (data.success && data.snapshot) {
      const prev8Array = Array.isArray(data.snapshot.prev8) ? data.snapshot.prev8 : (data.snapshot.prev8 ? Object.values(data.snapshot.prev8) : []);
      const next16Array = Array.isArray(data.snapshot.next16) ? data.snapshot.next16 : (data.snapshot.next16 ? Object.values(data.snapshot.next16) : []);
      
      const formatted = {
        success: data.success,
        snapshot: {
          cpuCoreId: data.snapshot.cpuCoreId,
          pc: `0x${toHex(data.snapshot.pc, 4)}`,
          registers: {
            a: `0x${toHex(data.snapshot.a)}`,
            x: `0x${toHex(data.snapshot.x)}`,
            y: `0x${toHex(data.snapshot.y)}`,
            p: `0x${toHex(data.snapshot.p)}`,
            sp: `0x${toHex(data.snapshot.sp, 4)}`
          },
          flags: {
            irq: data.snapshot.irq,
            nmi: data.snapshot.nmi,
            inPrgRom: data.snapshot.inPrgRom
          },
          prev8: prev8Array.length > 0 ? prev8Array.map(b => `0x${toHex(b)}`).join(' ') : 'N/A',
          next16: next16Array.length > 0 ? next16Array.slice(0, 8).map(b => `0x${toHex(b)}`).join(' ') + '...' : 'N/A'
        }
      };
      displayResult('imagineGetSnapshotResult', formatted);
    } else {
      displayResult('imagineGetSnapshotResult', data);
    }
  } catch (error) {
    displayResult('imagineGetSnapshotResult', `Error: ${error.message}`, true);
  }
}

// Test: Run Prediction
async function testImagineRunPrediction() {
  try {
    const data = await api.imagine.runPrediction();
    
    // Format predicted bytes for better display
    if (data.success && data.predictedBytes) {
      const bytesArray = Array.isArray(data.predictedBytes) ? data.predictedBytes : Object.values(data.predictedBytes);
      const formatted = {
        success: data.success,
        length: data.length,
        predictedBytes: bytesArray.map(b => `0x${toHex(b)}`).join(' ')
      };
      displayResult('imagineRunPredictionResult', formatted);
    } else {
      displayResult('imagineRunPredictionResult', data);
    }
  } catch (error) {
    displayResult('imagineRunPredictionResult', `Error: ${error.message}`, true);
  }
}

// Test: Apply Patch
async function testImagineApplyPatch() {
  const pcHex = document.getElementById('imaginePatchPc')?.value || '0x8000';
  const bytesStr = document.getElementById('imaginePatchBytes')?.value || '0xEA,0xEA';
  
  const pc = parseHex(pcHex);
  const bytes = bytesStr.split(',').map(b => parseHex(b.trim()));
  
  try {
    const data = await api.imagine.applyPatch(pc, bytes);
    displayResult('imagineApplyPatchResult', data);
  } catch (error) {
    displayResult('imagineApplyPatchResult', `Error: ${error.message}`, true);
  }
}

// Test: Imagine a Bug
async function testImagineABug() {
  try {
    const data = await api.imagine.imagineABug();
    
    // Format predicted bytes for better display
    if (data.success && data.predictedBytes) {
      const bytesArray = Array.isArray(data.predictedBytes) ? data.predictedBytes : Object.values(data.predictedBytes);
      const formatted = {
        success: data.success,
        message: data.message,
        predictedBytes: bytesArray.map(b => `0x${toHex(b)}`).join(' ')
      };
      displayResult('imagineABugResult', formatted);
    } else {
      displayResult('imagineABugResult', data);
    }
  } catch (error) {
    displayResult('imagineABugResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Predicted Bytes
async function testImagineGetPredictedBytes() {
  try {
    const data = await api.imagine.getPredictedBytes();
    
    // Format predicted bytes for better display
    if (data.success && data.predictedBytes) {
      const bytesArray = Array.isArray(data.predictedBytes) ? data.predictedBytes : Object.values(data.predictedBytes);
      const formatted = {
        success: data.success,
        length: data.length,
        predictedBytes: bytesArray.map(b => `0x${toHex(b)}`).join(' ')
      };
      displayResult('imagineGetPredictedBytesResult', formatted);
    } else {
      displayResult('imagineGetPredictedBytesResult', data);
    }
  } catch (error) {
    displayResult('imagineGetPredictedBytesResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Last Error
async function testImagineGetLastError() {
  try {
    const data = await api.imagine.getLastError();
    displayResult('imagineGetLastErrorResult', data);
  } catch (error) {
    displayResult('imagineGetLastErrorResult', `Error: ${error.message}`, true);
  }
}