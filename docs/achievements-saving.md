# Achievement Saving Implementation

## Overview

Achievements are now automatically saved to the game save when unlocked. This document describes the implementation and how to test it.

## Implementation Details

### Files Modified

1. **Windows/Webmodules/shared/achievements.js**
   - Added `saveAchievement(achievementId)` - Saves an achievement to the game save
   - Added `isAchievementSaved(achievementId)` - Checks if an achievement is already saved
   - Added `getSavedAchievements()` - Returns all saved achievement IDs

2. **Windows/Webmodules/AchievementsRuntime/script.js**
   - Modified `handleAchievementUnlock()` to call `lib.saveAchievement(achievementId)`

3. **Windows/Webmodules/AchievementsTest/script.js**
   - Modified `handleAchievementUnlock()` to call `window.achievementsLib.saveAchievement(achievementId)`

### How It Works

1. When an achievement is unlocked, `handleAchievementUnlock()` is called
2. The function calls `lib.saveAchievement(achievementId)` which:
   - Loads the current game save using `window.gameSave.load()`
   - Checks if the achievement is already saved (prevents duplicates)
   - Adds the achievement ID to the `Achievements` array
   - Saves back to storage using `window.gameSave.save()`
3. The saved achievements persist across sessions
4. Other webmodules (like DeckBuilder) can read the achievements from the game save

### Data Structure

Achievements are stored in the `GameSave` model as:

```csharp
public class GameSave
{
    // ...
    public List<string> Achievements { get; set; } = new();
    // ...
}
```

In JavaScript, this is accessed as:

```javascript
const save = await window.gameSave.load();
const achievementIds = save.Achievements; // Array of achievement ID strings
const starCount = save.Achievements.length; // Total achievements earned
```

## Testing

### Manual Testing in Browser Console

While in the AchievementsTest or AchievementsRuntime webmodule, you can test with:

```javascript
// Check if achievements library is loaded
console.log(window.achievementsLib);

// Manually save an achievement
await window.achievementsLib.saveAchievement('test-achievement-1');

// Check if it was saved
const isSaved = await window.achievementsLib.isAchievementSaved('test-achievement-1');
console.log('Is saved:', isSaved); // Should be true

// Get all saved achievements
const savedAchievements = await window.achievementsLib.getSavedAchievements();
console.log('All saved achievements:', savedAchievements);

// Check the full game save
const save = await window.gameSave.load();
console.log('Game save:', save);
console.log('Achievement count:', save.Achievements.length);
```

### Testing with Real Achievements

1. Load a ROM with RetroAchievements support
2. Start playing and earn an achievement
3. Watch the console logs:
   - `Achievement unlocked: [ID]`
   - `🎉 Achievement Unlocked: [Title]`
   - `[achievementsLib] Saving achievement [ID], total: [count]`
   - `[achievementsLib] Achievement [ID] saved successfully`
4. Check the game save in browser console:
   ```javascript
   const save = await window.gameSave.load();
   console.log('Achievements:', save.Achievements);
   ```
5. Reload the application/webmodule
6. Verify achievements persist:
   ```javascript
   const saved = await window.achievementsLib.getSavedAchievements();
   console.log('Persisted achievements:', saved);
   ```

### Testing in DeckBuilder

The DeckBuilder displays the achievement count as "stars":

1. Open DeckBuilder webmodule
2. The achievement star count should match the number of saved achievements
3. You can verify this with:
   ```javascript
   const save = await window.gameSave.load();
   console.log('Stars (achievements):', save.Achievements.length);
   ```

### Clearing Test Data

To reset and clear all saved achievements:

```javascript
// Method 1: Clear just achievements
const save = await window.gameSave.load();
save.Achievements = [];
await window.gameSave.save(save);

// Method 2: Full reset (resets everything)
await window.gameSave.reset();
```

## Dependencies

The achievement saving functionality requires these scripts to be loaded (in order):

1. `shared/storage.js` - Storage abstraction layer
2. `shared/gameSave.js` - Game save management
3. `shared/webapi.js` - Web API bridge
4. `shared/achievements.js` - Achievement utilities (includes save functions)

All achievement-related webmodules already include these dependencies in their `index.html`.

## Error Handling

The implementation includes comprehensive error handling:

- If `gameSave` is not available, logs a warning and returns false
- If an achievement is already saved, skips the duplicate and logs info
- All errors are caught and logged to console
- Failed saves return `false` instead of throwing exceptions

## Future Enhancements

Possible improvements for future versions:

1. **Achievement Sync**: Sync achievements with RetroAchievements server
2. **Achievement Analytics**: Track when achievements were earned (timestamps)
3. **Achievement Progress**: Save intermediate progress for multi-step achievements
4. **Achievement Rewards**: Automatically unlock cores/features based on achievements
5. **Achievement Notifications**: Desktop notifications for achievements
