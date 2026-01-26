// AchievementsRuntime - Overlay Mode Script
(function () {
  'use strict';

  const api = window.webapi;
  const lib = window.achievementsLib;
  let achievementsList = [];
  let isInitialized = false;

  // DOM Elements
  const achievementsOverlay = document.querySelector('.achievements-overlay');

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

  // Initialize on page load
  function init() {
    autoInitialize();
  }

  // Auto-initialize achievements on page load
  async function autoInitialize() {
    achievementsOverlay.innerHTML = '<div class="loading">Loading...</div>';
    
    // Small delay to let UI render
    await new Promise(resolve => setTimeout(resolve, 100));
    await initializeAchievements();
  }

  // Initialize achievements
  async function initializeAchievements() {
    try {
      const result = await api.achievements.init();

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
        const errorMsg = result.error || 'Failed to initialize';
        console.error('Achievement initialization failed:', errorMsg);
        achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMsg)}</div>`;
      }
    } catch (error) {
      console.error('Achievement initialization error:', error);
      const errorMsg = error?.message || 'Error loading';
      achievementsOverlay.innerHTML = `<div class="empty-state">${lib.escapeHtml(errorMsg)}</div>`;
    }
  }

  // Refresh achievements list
  async function refreshAchievementsList() {
    if (!isInitialized) {
      return;
    }

    try {
      const result = await api.achievements.getList();

      if (result.success && result.achievements) {
        achievementsList = result.achievements;
        
        if (achievementsList.length === 0) {
          achievementsOverlay.innerHTML = '<div class="empty-state">No achievements</div>';
        } else {
          displayAchievements(achievementsList);
        }
      } else {
        achievementsOverlay.innerHTML = '<div class="empty-state">Load failed</div>';
      }
    } catch (error) {
      achievementsOverlay.innerHTML = '<div class="empty-state">Error</div>';
      console.error('Error loading achievements:', error);
    }
  }

  // Display achievements in the overlay
  function displayAchievements(achievements) {
    achievementsOverlay.innerHTML = '';

    // Only show unlocked achievements in overlay mode
    const unlockedAchievements = achievements.filter(achievement => 
      lib.isAchievementCompleted(achievement)
    );

    if (unlockedAchievements.length === 0) {
      achievementsOverlay.innerHTML = '<div class="empty-state">No achievements unlocked yet</div>';
      return;
    }

    unlockedAchievements.forEach(achievement => {
      const card = createAchievementCard(achievement);
      achievementsOverlay.appendChild(card);
    });
  }

  // Create achievement card element (two-line version)
  function createAchievementCard(achievement) {
    const isCompleted = lib.isAchievementCompleted(achievement);
    const card = document.createElement('div');
    card.className = `achievement-card ${isCompleted ? 'completed' : 'locked'}`;
    
    const icon = isCompleted ? '✓' : '○';
    const title = lib.getAchievementTitle(achievement);
    const description = lib.getAchievementDescription(achievement);

    card.innerHTML = `
      <div class="achievement-header">
        <span class="achievement-icon">${icon}</span>
        <span class="achievement-title">${lib.escapeHtml(title)}</span>
      </div>
      <div class="achievement-description">${lib.escapeHtml(description)}</div>
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
    
    // Play a random VictorySfx (VictorySong1.mp3 through VictorySong5.mp3)
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
      
      // Hide modal after duration
      setTimeout(() => {
        achievementModal.style.display = 'none';
        resolve();
      }, MODAL_DISPLAY_DURATION);
    });
  }

  // Start the app
  document.addEventListener('DOMContentLoaded', init);
})();
