// shared/achievements.js - Shared achievement utilities
(function() {
  'use strict';

  const achievementsLib = {
    // Helper to check if achievement is completed
    isAchievementCompleted(achievement) {
      return achievement.isCompleted || 
             achievement.IsCompleted || 
             achievement.completed || 
             achievement.Completed ||
             achievement.awarded ||
             achievement.Awarded ||
             false;
    },

    // Helper to get achievement title
    getAchievementTitle(achievement) {
      return achievement.title || 
             achievement.Title || 
             achievement.name || 
             achievement.Name || 
             'Unknown Achievement';
    },

    // Helper to get achievement description
    getAchievementDescription(achievement) {
      return achievement.description || 
             achievement.Description || 
             achievement.desc || 
             achievement.Desc ||
             'No description available';
    },

    // Helper to get achievement points
    getAchievementPoints(achievement) {
      return achievement.points || 
             achievement.Points || 
             achievement.score || 
             achievement.Score ||
             0;
    },

    // Helper to get achievement ID
    getAchievementId(achievement) {
      return achievement.id || 
             achievement.Id || 
             achievement.ID || 
             achievement.achievementId ||
             achievement.AchievementId ||
             'Unknown';
    },

    // Escape HTML to prevent XSS
    escapeHtml(text) {
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    },

    // Save current APU channel state and disable all except noise channel
    async saveAndDisableApuChannels() {
      try {
        // Channel mask bits: 0x01=Pulse1, 0x02=Pulse2, 0x04=Triangle, 0x08=Noise, 0x10=DMC
        // We only want the noise channel (bit 3), so mask = 0x08
        const noiseOnlyMask = 0x08;
        await window.webapi.apu.setChannelEnableMask(noiseOnlyMask);
        console.log('APU channels disabled (noise only) for victory song');
      } catch (error) {
        console.error('Error disabling APU channels:', error);
      }
    },

    // Restore all APU channels sequentially with delay
    async restoreApuChannels() {
      try {
        const channelDelay = 240; // milliseconds between each channel restoration
        
        // Channel bits: 0x01=Pulse1, 0x02=Pulse2, 0x04=Triangle, 0x08=Noise, 0x10=DMC
        // Restore them one by one by building up the mask
        const channelSteps = [
          { mask: 0x01, name: 'Pulse1' },
          { mask: 0x03, name: 'Pulse2' },
          { mask: 0x07, name: 'Triangle' },
          { mask: 0x0F, name: 'Noise' },
          { mask: 0x1F, name: 'DMC' }
        ];
        
        for (const step of channelSteps) {
          await window.webapi.apu.setChannelEnableMask(step.mask);
          console.log(`APU channel restored: ${step.name}`);
          
          // Delay before next channel (except after the last one)
          if (step.mask !== 0x1F) {
            await new Promise(resolve => setTimeout(resolve, channelDelay));
          }
        }
        
        console.log('All APU channels restored');
      } catch (error) {
        console.error('Error restoring APU channels:', error);
      }
    },

    // Monitor victory song completion and restore channels when done
    monitorVictorySongCompletion(victoryFilename) {
      const checkInterval = 200; // Check every 200ms
      let consecutiveNotPlaying = 0;
      const requiredNotPlayingChecks = 3; // Require 3 consecutive checks showing not playing
      let restorationStarted = false;

      // Start restoration 2 seconds before the song is expected to finish
      // Assuming victory songs are ~5 seconds long, start restoration at 3 seconds
      const estimatedSongDuration = 5000; // milliseconds
      const restorationLeadTime = 2000; // Start 2 seconds early
      const restorationStartDelay = estimatedSongDuration - restorationLeadTime;

      // Schedule restoration to start before song ends
      const restorationTimer = setTimeout(async () => {
        if (!restorationStarted) {
          restorationStarted = true;
          await this.restoreApuChannels();
          console.log('APU channels restoration initiated (2s before song end)');
        }
      }, restorationStartDelay);

      const intervalId = setInterval(async () => {
        try {
          const status = await window.webapi.audio.getStatus();
          
          // Check if the victory song is still playing
          const isVictorySongPlaying = status.success && 
                                       status.currentMusicFile === victoryFilename && 
                                       status.isMusicPlaying;

          if (!isVictorySongPlaying) {
            consecutiveNotPlaying++;
            
            // Only restore after multiple consecutive checks to avoid race conditions
            if (consecutiveNotPlaying >= requiredNotPlayingChecks) {
              clearInterval(intervalId);
              clearTimeout(restorationTimer);
              
              // If restoration hasn't started yet, do it now
              if (!restorationStarted) {
                restorationStarted = true;
                await this.restoreApuChannels();
              }
              console.log('Victory song completed, monitoring stopped');
            }
          } else {
            // Reset counter if we detect it's still playing
            consecutiveNotPlaying = 0;
          }
        } catch (error) {
          console.error('Error monitoring victory song:', error);
          // On error, restore channels and stop monitoring
          clearInterval(intervalId);
          clearTimeout(restorationTimer);
          if (!restorationStarted) {
            restorationStarted = true;
            await this.restoreApuChannels();
          }
        }
      }, checkInterval);

      // Safety timeout: restore channels after 30 seconds regardless
      setTimeout(async () => {
        clearInterval(intervalId);
        clearTimeout(restorationTimer);
        if (!restorationStarted) {
          restorationStarted = true;
          await this.restoreApuChannels();
          console.log('Victory song timeout reached, APU channels restored');
        }
      }, 30000);
    },

    // Play a random victory sound effect
    async playRandomVictorySfx() {
      try {
        // Pick a random VictorySong (1-5)
        const songNumber = Math.floor(Math.random() * 5) + 1;
        const filename = `VictorySong${songNumber}.mp3`;
        
        // Save current APU channel state and disable all except noise (bit 3)
        await this.saveAndDisableApuChannels();
        
        // Play using the audio API (music endpoint for victory songs)
        const result = await window.webapi.audio.playMusic(filename, false); // Don't loop victory songs
        
        if (!result.success) {
          console.warn('Failed to play victory sound:', result.error);
          // Restore channels immediately if playback failed
          await this.restoreApuChannels();
          return;
        }

        // Monitor the song until it finishes, then restore APU channels
        this.monitorVictorySongCompletion(filename);
      } catch (error) {
        console.error('Error playing victory sound:', error);
        // Restore channels on error
        await this.restoreApuChannels();
      }
    },

    // Save an achievement to the game save
    async saveAchievement(achievementId) {
      try {
        // Check if gameSave is available
        if (!window.gameSave || typeof window.gameSave.load !== 'function') {
          console.warn('[achievementsLib] gameSave not available, cannot save achievement');
          return false;
        }

        // Load the current save
        const save = await window.gameSave.load();

        // Ensure Achievements array exists
        if (!save.Achievements) {
          save.Achievements = [];
        }

        // Check if achievement is already saved
        if (save.Achievements.includes(achievementId)) {
          console.log(`[achievementsLib] Achievement ${achievementId} already saved`);
          return true;
        }

        // Add the achievement
        save.Achievements.push(achievementId);
        
        console.log(`[achievementsLib] Saving achievement ${achievementId}, total: ${save.Achievements.length}`);

        // Save back to storage
        const success = await window.gameSave.save(save);
        
        if (success) {
          console.log(`[achievementsLib] Achievement ${achievementId} saved successfully`);
        } else {
          console.error(`[achievementsLib] Failed to save achievement ${achievementId}`);
        }

        return success;
      } catch (error) {
        console.error('[achievementsLib] Error saving achievement:', error);
        return false;
      }
    },

    // Check if an achievement is already saved in the game save
    async isAchievementSaved(achievementId) {
      try {
        if (!window.gameSave || typeof window.gameSave.load !== 'function') {
          return false;
        }

        const save = await window.gameSave.load();
        return save.Achievements && save.Achievements.includes(achievementId);
      } catch (error) {
        console.error('[achievementsLib] Error checking saved achievement:', error);
        return false;
      }
    },

    // Get all saved achievements from the game save
    async getSavedAchievements() {
      try {
        if (!window.gameSave || typeof window.gameSave.load !== 'function') {
          return [];
        }

        const save = await window.gameSave.load();
        return save.Achievements || [];
      } catch (error) {
        console.error('[achievementsLib] Error getting saved achievements:', error);
        return [];
      }
    }
  };

  // Expose to global scope
  window.achievementsLib = achievementsLib;
})();
