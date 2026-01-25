// ========== Achievement Tests ==========

// Test: Initialize Achievements Engine
async function testAchInit() {
  try {
    const data = await api.achievements.init();
    displayResult('achInitResult', data);
  } catch (error) {
    displayResult('achInitResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Achievement List
async function testAchGetList() {
  try {
    const data = await api.achievements.getList();
    displayResult('achGetListResult', data);
  } catch (error) {
    displayResult('achGetListResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Achievement State
async function testAchGetState() {
  const id = document.getElementById('achStateId')?.value || '1';
  
  try {
    const data = await api.achievements.getState(id);
    displayResult('achGetStateResult', data);
  } catch (error) {
    displayResult('achGetStateResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Achievement Progress
async function testAchGetProgress() {
  const id = document.getElementById('achProgressId')?.value || '1';
  
  try {
    const data = await api.achievements.getProgress(id);
    displayResult('achGetProgressResult', data);
  } catch (error) {
    displayResult('achGetProgressResult', `Error: ${error.message}`, true);
  }
}

// Test: Get Achievement Conditions
async function testAchGetConditions() {
  const id = document.getElementById('achConditionsId')?.value || '1';
  
  try {
    const data = await api.achievements.getConditions(id);
    displayResult('achGetConditionsResult', data);
  } catch (error) {
    displayResult('achGetConditionsResult', `Error: ${error.message}`, true);
  }
}

// Test: Force Complete Achievement
async function testAchForceComplete() {
  const id = document.getElementById('achForceCompleteId')?.value || '1';
  
  try {
    const data = await api.achievements.forceComplete(id);
    displayResult('achForceCompleteResult', data);
  } catch (error) {
    displayResult('achForceCompleteResult', `Error: ${error.message}`, true);
  }
}

// Test: Evaluate Achievements Frame
async function testAchEvaluate() {
  try {
    const data = await api.achievements.evaluateFrame();
    displayResult('achEvaluateResult', data);
  } catch (error) {
    displayResult('achEvaluateResult', `Error: ${error.message}`, true);
  }
}
