// AchievementsTest - Main Script
(function () {
  'use strict';

  const api = window.webapi;
  const lib = window.achievementsLib;
  let achievementsList = [];
  let isInitialized = false;

  // DOM Elements
  const btnInit = document.getElementById('btnInit');
  const btnRefresh = document.getElementById('btnRefresh');
  const btnMonitor = document.getElementById('btnMonitor');
  const btnResetAchievements = document.getElementById('btnResetAchievements');
  const statusMessage = document.getElementById('statusMessage');
  const achievementsContainer = document.getElementById('achievementsContainer');

  // Monitoring state
  let isMonitoring = false;
  let monitoringInterval = null;
  let knownUnlockedAchievements = new Set();

  // Modal queue management
  const MODAL_DISPLAY_DURATION = 3000; // milliseconds (configurable)
  let modalQueue = [];
  let isModalDisplaying = false;
  const achievementModal = document.getElementById('achievementModal');
  const modalAchievementName = document.getElementById('modalAchievementName');
  const modalProgressBar = document.getElementById('modalProgressBar');

  // Initialize event listeners
  function init() {
    btnInit.addEventListener('click', initializeAchievements);
    btnRefresh.addEventListener('click', refreshAchievementsList);
    btnMonitor.addEventListener('click', toggleMonitoring);
    btnResetAchievements.addEventListener('click', resetAchievements);
    
    // Auto-initialize on load
    autoInitialize();
  }

  // Auto-initialize achievements on page load
  async function autoInitialize() {
    achievementsContainer.innerHTML = '<div class="loading">Initializing achievements</div>';
    
    // Small delay to let UI render
    await new Promise(resolve => setTimeout(resolve, 100));
    await initializeAchievements();
  }

  // Initialize achievements
  async function initializeAchievements() {
    try {
      const completedIds = await lib.getSavedAchievements();
      const result = await api.achievements.init({ completedIds });

      if (result.success) {
        isInitialized = true;
        
        // Automatically load the achievements list after initialization
        await refreshAchievementsList();
        
        // Track currently unlocked achievements
        initializeKnownUnlocked();
        
        // Auto-start monitoring
        startMonitoring();
      } else {
        // Display the actual error message from the server
        const errorMsg = result.error || 'Failed to initialize achievements. Make sure a game is loaded.';
        console.error('Achievement initialization failed:', errorMsg);
        achievementsContainer.innerHTML = `<p class="empty-state">${lib.escapeHtml(errorMsg)}</p>`;
      }
    } catch (error) {
      console.error('Achievement initialization error:', error);
      const errorMsg = error?.message || 'Error during initialization. Check console for details.';
      achievementsContainer.innerHTML = `<p class="empty-state">${lib.escapeHtml(errorMsg)}</p>`;
    }
  }

  // Refresh achievements list
  async function refreshAchievementsList() {
    if (!isInitialized) {
      return;
    }

    try {
      // Show loading state
      achievementsContainer.innerHTML = '<div class="loading">Loading achievements</div>';

      const result = await api.achievements.getList();

      // Debug: Log the actual structure
      console.log('Achievements API result:', result);
      if (result.achievements && result.achievements.length > 0) {
        console.log('First achievement structure:', result.achievements[0]);
        console.log('First achievement as JSON:', JSON.stringify(result.achievements[0], null, 2));
        console.log('All keys:', Object.keys(result.achievements[0]));
      }

      if (result.success && result.achievements) {
        achievementsList = result.achievements;
        
        if (achievementsList.length === 0) {
          achievementsContainer.innerHTML = '<p class="empty-state">No achievements found for the current game</p>';
        } else {
          displayAchievements(achievementsList);
        }
      } else {
        achievementsContainer.innerHTML = '<p class="empty-state">Failed to load achievements. Engine may not be initialized.</p>';
      }
    } catch (error) {
      achievementsContainer.innerHTML = '<p class="empty-state">Error loading achievements</p>';
      console.error('Error loading achievements:', error);
    }
  }

  // Display achievements in the UI
  function displayAchievements(achievements) {
    achievementsContainer.innerHTML = '';

    achievements.forEach(achievement => {
      const card = createAchievementCard(achievement);
      achievementsContainer.appendChild(card);
    });
  }

  // Create achievement card element
  function createAchievementCard(achievement) {
    const isCompleted = lib.isAchievementCompleted(achievement);
    const card = document.createElement('div');
    card.className = `achievement-card ${isCompleted ? 'completed' : 'locked'}`;
    card.title = lib.getAchievementDescription(achievement); // Show description on hover
    
    const icon = isCompleted ? '✓' : '○';

    card.innerHTML = `
      <span class="achievement-icon">${icon}</span>
      <span class="achievement-title">${lib.escapeHtml(lib.getAchievementTitle(achievement))}</span>
    `;

    return card;
  }

  // Initialize known unlocked achievements
  function initializeKnownUnlocked() {
    knownUnlockedAchievements.clear();
    achievementsList.forEach(achievement => {
      if (lib.isAchievementCompleted(achievement)) {
        const id = lib.getAchievementId(achievement);
        knownUnlockedAchievements.add(id);
      }
    });
  }

  // Toggle achievement monitoring
  async function toggleMonitoring() {
    if (!isInitialized) {
      return;
    }

    if (isMonitoring) {
      stopMonitoring();
    } else {
      startMonitoring();
    }
  }

  // Start monitoring for achievement unlocks
  function startMonitoring() {
    isMonitoring = true;

    // Poll for achievement updates every 100ms (10 times per second)
    monitoringInterval = setInterval(async () => {
      try {
        await evaluateAchievementFrame();
      } catch (error) {
        console.error('Error during monitoring:', error);
      }
    }, 100);
  }

  // Stop monitoring
  function stopMonitoring() {
    isMonitoring = false;
    if (monitoringInterval) {
      clearInterval(monitoringInterval);
      monitoringInterval = null;
    }
  }

  // Evaluate current frame for achievements
  async function evaluateAchievementFrame() {
    try {
      const result = await api.achievements.evaluateFrame();
      
      if (result.success && result.unlockedThisFrame && result.unlockedThisFrame.length > 0) {
        // New achievements unlocked!
        for (const achievementId of result.unlockedThisFrame) {
          if (!knownUnlockedAchievements.has(achievementId)) {
            knownUnlockedAchievements.add(achievementId);
            await handleAchievementUnlock(achievementId);
          }
        }
        
        // Refresh the list to show updated states
        await refreshAchievementsList();
      }
    } catch (error) {
      console.error('Error evaluating achievement frame:', error);
    }
  }

  // Handle achievement unlock event
  async function handleAchievementUnlock(achievementId) {
    console.log(`Achievement unlocked: ${achievementId}`);
    
    // Find the achievement details
    const achievement = achievementsList.find(a => lib.getAchievementId(a) === achievementId);
    const title = achievement ? lib.getAchievementTitle(achievement) : achievementId;
    
    console.log(`🎉 Achievement Unlocked: ${title}`);
    
    // Save the achievement to game save
    await lib.saveAchievement(achievementId);
    
    // Queue the achievement modal
    queueAchievementModal(title);
    
    // Play a random VictorySfx with full orchestration (disables channels, plays, gradually restores)
    await lib.playRandomVictorySfx();
  }

  // Queue an achievement modal for display
  function queueAchievementModal(achievementTitle) {
    modalQueue.push(achievementTitle);
    
    // If no modal is currently displaying, start processing the queue
    if (!isModalDisplaying) {
      processModalQueue();
    }
  }

  // Process the modal queue
  async function processModalQueue() {
    if (modalQueue.length === 0) {
      isModalDisplaying = false;
      return;
    }

    isModalDisplaying = true;
    const achievementTitle = modalQueue.shift();
    
    // Display the modal
    await displayAchievementModal(achievementTitle);
    
    // Process the next item in the queue
    processModalQueue();
  }

  // Display achievement unlock modal with progress bar
  function displayAchievementModal(achievementTitle) {
    return new Promise((resolve) => {
      // Set the achievement name
      modalAchievementName.textContent = achievementTitle;
      
      // Reset progress bar
      modalProgressBar.style.width = '0%';
      modalProgressBar.style.transition = 'none';
      
      // Show the modal
      achievementModal.style.display = 'block';
      
      // Force reflow to ensure the transition works
      void modalProgressBar.offsetWidth;
      
      // Animate progress bar
      modalProgressBar.style.transition = `width ${MODAL_DISPLAY_DURATION}ms linear`;
      modalProgressBar.style.width = '100%';
      
      // Hide modal after display duration
      setTimeout(() => {
        achievementModal.style.display = 'none';
        resolve();
      }, MODAL_DISPLAY_DURATION);
    });
  }

  // Reset all achievements for the current game
  async function resetAchievements() {
    if (!isInitialized) {
      console.warn('Cannot reset achievements: engine not initialized');
      return;
    }

    // Confirm with user
    if (!confirm('Are you sure you want to reset all achievements for the current game? This cannot be undone.')) {
      return;
    }

    try {
      // Disable button during reset
      btnResetAchievements.disabled = true;
      btnResetAchievements.textContent = 'Resetting...';

      // Step 1: Reset the achievements engine on the backend
      const result = await api.achievements.reset();
      
      if (!result.success) {
        console.error('Failed to reset achievements engine:', result.error);
        alert(`Failed to reset achievements: ${result.error}`);
        return;
      }

      console.log('Achievements engine reset successfully');

      // Step 2: Clear saved achievements from game save
      if (window.gameSave && typeof window.gameSave.load === 'function') {
        try {
          const save = await window.gameSave.load();
          save.Achievements = [];
          const saveSuccess = await window.gameSave.save(save);
          
          if (saveSuccess) {
            console.log('Game save achievements cleared successfully');
          } else {
            console.warn('Failed to clear game save achievements');
          }
        } catch (saveError) {
          console.error('Error clearing game save achievements:', saveError);
        }
      }

      // Step 3: Reset local tracking
      knownUnlockedAchievements.clear();

      // Step 4: Refresh the display
      await refreshAchievementsList();

      console.log('✅ All achievements reset successfully');
    } catch (error) {
      console.error('Error resetting achievements:', error);
      alert('An error occurred while resetting achievements. See console for details.');
    } finally {
      // Re-enable button
      btnResetAchievements.disabled = false;
      btnResetAchievements.textContent = 'Reset Achievements';
    }
  }

  // Start the app
  document.addEventListener('DOMContentLoaded', init);
})();
