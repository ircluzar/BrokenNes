// AchievementsTest - Main Script
(function () {
  'use strict';

  const api = window.webapi;
  let achievementsList = [];
  let isInitialized = false;

  // DOM Elements
  const btnInit = document.getElementById('btnInit');
  const btnRefresh = document.getElementById('btnRefresh');
  const statusMessage = document.getElementById('statusMessage');
  const achievementsContainer = document.getElementById('achievementsContainer');

  // Initialize event listeners
  function init() {
    btnInit.addEventListener('click', initializeAchievements);
    btnRefresh.addEventListener('click', refreshAchievementsList);
    
    // Disable refresh button until initialized
    btnRefresh.disabled = true;
  }

  // Show status message
  function showStatus(message, type = 'info') {
    statusMessage.textContent = message;
    statusMessage.className = `status-message ${type}`;
  }

  // Initialize achievements
  async function initializeAchievements() {
    try {
      btnInit.disabled = true;
      btnRefresh.disabled = true;
      showStatus('Initializing achievements...', 'info');

      const result = await api.achievements.init();

      if (result.success) {
        isInitialized = true;
        showStatus('Achievements initialized! Loading list...', 'success');
        
        // Automatically load the achievements list after initialization
        await refreshAchievementsList();
        
        // Enable refresh button after successful initialization
        btnRefresh.disabled = false;
      } else {
        showStatus(`Initialization failed: ${result.error || result.message || 'Unknown error'}`, 'error');
        achievementsContainer.innerHTML = '<p class="empty-state">Failed to initialize achievements. Make sure a game is loaded.</p>';
      }
    } catch (error) {
      showStatus(`Error: ${error.message}`, 'error');
      console.error('Achievement initialization error:', error);
      achievementsContainer.innerHTML = '<p class="empty-state">Error during initialization. Check console for details.</p>';
    } finally {
      btnInit.disabled = false;
    }
  }

  // Refresh achievements list
  async function refreshAchievementsList() {
    if (!isInitialized) {
      showStatus('Please initialize achievements first', 'error');
      return;
    }

    try {
      btnRefresh.disabled = true;
      showStatus('Loading achievements...', 'info');

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
          showStatus('No achievements available', 'info');
        } else {
          displayAchievements(achievementsList);
          const completedCount = achievementsList.filter(a => isAchievementCompleted(a)).length;
          showStatus(`Loaded ${achievementsList.length} achievements (${completedCount} completed)`, 'success');
        }
      } else {
        achievementsContainer.innerHTML = '<p class="empty-state">Failed to load achievements. Engine may not be initialized.</p>';
        showStatus(`Failed to load achievements: ${result.error || result.message || 'Engine not initialized'}`, 'error');
      }
    } catch (error) {
      achievementsContainer.innerHTML = '<p class="empty-state">Error loading achievements</p>';
      showStatus(`Error: ${error.message}`, 'error');
      console.error('Error loading achievements:', error);
    } finally {
      btnRefresh.disabled = false;
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

  // Helper to check if achievement is completed
  function isAchievementCompleted(achievement) {
    // Try multiple possible field names
    return achievement.isCompleted || 
           achievement.IsCompleted || 
           achievement.completed || 
           achievement.Completed ||
           achievement.awarded ||
           achievement.Awarded ||
           false;
  }

  // Helper to get achievement title
  function getAchievementTitle(achievement) {
    return achievement.title || 
           achievement.Title || 
           achievement.name || 
           achievement.Name || 
           'Unknown Achievement';
  }

  // Helper to get achievement description
  function getAchievementDescription(achievement) {
    return achievement.description || 
           achievement.Description || 
           achievement.desc || 
           achievement.Desc ||
           'No description available';
  }

  // Helper to get achievement points
  function getAchievementPoints(achievement) {
    return achievement.points || 
           achievement.Points || 
           achievement.score || 
           achievement.Score ||
           0;
  }

  // Helper to get achievement ID
  function getAchievementId(achievement) {
    return achievement.id || 
           achievement.Id || 
           achievement.ID || 
           achievement.achievementId ||
           achievement.AchievementId ||
           'Unknown';
  }

  // Create achievement card element
  function createAchievementCard(achievement) {
    const isCompleted = isAchievementCompleted(achievement);
    const card = document.createElement('div');
    card.className = `achievement-card ${isCompleted ? 'completed' : 'locked'}`;
    
    const icon = getAchievementIcon(achievement);
    const statusBadge = isCompleted ? 
      '<span class="status-badge completed">Completed</span>' : 
      '<span class="status-badge locked">Locked</span>';

    card.innerHTML = `
      <div class="achievement-header">
        <div class="achievement-icon">${icon}</div>
        <div class="achievement-info">
          <h3 class="achievement-title">${escapeHtml(getAchievementTitle(achievement))}</h3>
          <p class="achievement-points">⭐ ${getAchievementPoints(achievement)} points</p>
        </div>
      </div>
      <p class="achievement-description">${escapeHtml(getAchievementDescription(achievement))}</p>
      <div class="achievement-status">
        ${statusBadge}
        <span class="achievement-id">ID: ${getAchievementId(achievement)}</span>
      </div>
    `;

    return card;
  }

  // Get achievement icon (trophy emoji or custom based on state)
  function getAchievementIcon(achievement) {
    if (achievement.isCompleted) {
      return '🏆';
    }
    return '🔒';
  }

  // Escape HTML to prevent XSS
  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  // Start the app
  document.addEventListener('DOMContentLoaded', init);
})();
