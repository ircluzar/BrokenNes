# Continue Project - Workflow Engine Design

## Overview

The Continue project implements a progression-based workflow engine for the BrokenNes emulator. Players progress through levels by completing achievements in NES games using specific emulator component combinations (cards). This document outlines the core systems, data flow, and implementation details.

## Core Systems

### 1. Achievement System

#### Achievement Stars
- **Purpose**: Primary progression currency
- **Representation**: Star counter in UI
- **Function**: Unlocks ability to progress to next level
- **Constraints**: 
  - Can only be unlocked once per game save
  - Only compatible games can award achievements
  - Achievements have unique IDs (uppercase with `_` delimiters)

#### Achievement Monitoring
- **Scope**: Limited subset of achievements monitored per session
- **Implementation**: C# side monitors specific memory addresses
- **Lifecycle**:
  1. Achievement assigned to emulation instance
  2. C# monitors memory conditions via formula evaluation
  3. Achievement triggers → removed from monitoring
  4. Message sent to JavaScript layer
  5. Game save updated
  6. Achievement notification displayed

#### Game Compatibility
- **Identification**: Unique ID matching (potentially from iNES header)
- **Database**: Bundled database contains compatibility info
- **States**:
  - Compatible games with achievements
  - Compatible games without achievements  
  - Incompatible games

### 2. Pre-Registration System

#### ROM Listing Interface
- **Location**: Separate from main emulator ROM manager
- **Features**:
  - Enhanced game information display
  - Bundled database integration
  - Achievement compatibility indicators
- **Data Source**: Local bundled database

### 3. Database System

#### Database Format
- **Storage**: JSON export/import capability
- **Runtime**: In-memory with IndexedDB persistence
- **Management**: Debug CRUD interface for data manipulation

#### Database Schema
```json
{
  "games": [
    {
      "id": "unique_game_id",
      "name": "Game Title",
      "compatible": true,
      "achievements": [
        {
          "id": "ACHIEVEMENT_ID",
          "name": "Achievement Name",
          "description": "Achievement description",
          "formula": "memory_monitoring_formula"
        }
      ]
    }
  ]
}
```

#### Database Workflow
1. **Development**: Work on local database via debug CRUD
2. **Export**: Export altered database to JSON
3. **Promotion**: Replace bundled database in repository
4. **Distribution**: Updated database ships with application

#### Formula System
- **Storage**: String format in database
- **Parsing**: C# converts string to monitoring object
- **Evaluation**: C# monitors game memory based on formula
- **Trigger**: Achievement completion removes watcher, notifies JavaScript

### 4. Level System

#### Level Progression
- **Frequency**: Every 4th level introduces new required cards
- **Card Assignment**: Fixed emulator component cards given to player
- **Constraint**: Must use assigned cards to complete achievements

#### Level Structure
- **Fixed Card**: One mandatory core card per level
- **Optional Cards**: Player can use owned cards for other core slots
- **Console Generation**: Cards combine to create emulator configuration
- **ROM Assignment**: Player selects achievement-compatible ROM
- **Achievement Pool**: 5 random achievements assigned per level

### 5. Game Loop

#### Level Setup Phase
1. **Card Assignment**: System assigns required card for level
2. **Console Configuration**: Player configures remaining card slots
3. **ROM Selection**: Player chooses compatible ROM with available achievements
4. **Achievement Assignment**: System selects 5 random achievements for monitoring

#### Emulation Phase
1. **Launch**: ROM boots with pre-configured emulator setup
2. **Display Mode**: Game runs in display mode
3. **Achievement UI**: Small list shows currently monitored achievements
4. **Monitoring**: C# layer monitors memory for achievement conditions

#### Completion Phase
1. **Achievement Trigger**: Single achievement completion per session
2. **Emulation Pause**: Emulator pauses on achievement unlock
3. **Notification**: "Achievement Get!" message displayed
4. **Return**: Automatic return to Continue page
5. **Progress Check**: Evaluate if level completion threshold met

#### Level Transition
1. **Threshold Check**: Verify sufficient achievements for next level
2. **Card Reward**: Assign new cards to player inventory
3. **Level Increment**: Progress to next level
4. **Loop Reset**: Begin new level setup phase

## State Management

### Game Save Structure
```json
{
  "currentLevel": 1,
  "achievementStars": 0,
  "unlockedAchievements": ["ACHIEVEMENT_ID_1", "ACHIEVEMENT_ID_2"],
  "ownedCards": ["card_id_1", "card_id_2"],
  "completedLevels": [1, 2, 3]
}
```

### Progression State
- **Current Level**: Active level number
- **Star Count**: Total achievement stars earned
- **Card Inventory**: Available emulator component cards
- **Achievement History**: Completed achievements across all games

## Implementation Architecture

### C# Side Responsibilities
- Memory monitoring and formula evaluation
- Achievement condition detection
- Emulator core integration
- JavaScript communication via messages

### JavaScript Side Responsibilities
- UI state management
- Database operations (IndexedDB)
- Level progression logic
- Achievement assignment and tracking

### Communication Protocol
```javascript
// C# to JavaScript
{
  "type": "ACHIEVEMENT_UNLOCKED",
  "achievementId": "ACHIEVEMENT_ID",
  "gameId": "game_unique_id"
}

// JavaScript to C#
{
  "type": "START_MONITORING",
  "achievements": [
    {
      "id": "ACHIEVEMENT_ID",
      "formula": "memory_formula_string"
    }
  ]
}
```

## Data Flow

### Achievement Unlock Flow
1. **Setup**: JavaScript sends achievement formulas to C#
2. **Monitoring**: C# evaluates memory conditions during emulation
3. **Trigger**: Achievement condition met
4. **Notification**: C# sends unlock message to JavaScript
5. **Update**: JavaScript updates game save and UI
6. **Cleanup**: C# removes achievement from monitoring
7. **Return**: User returned to Continue page

### Level Progress Flow
1. **Check**: Evaluate achievement star count against level requirements
2. **Transition**: If threshold met, advance to next level
3. **Reward**: Add new cards to player inventory
4. **Setup**: Configure next level requirements and constraints

## Technical Considerations

### Performance
- **Memory Monitoring**: Efficient C# memory watchers
- **Database Operations**: Optimized IndexedDB queries
- **UI Updates**: Minimal re-renders on state changes

### Scalability
- **Achievement Database**: Structured for easy expansion
- **Card System**: Modular card definitions
- **Level System**: Configurable level requirements

### Error Handling
- **Invalid ROMs**: Graceful handling of incompatible games
- **Memory Errors**: Robust formula evaluation with fallbacks
- **Database Corruption**: Recovery mechanisms for corrupted saves

## Future Enhancements

### Achievement System
- **Categories**: Different types of achievements (speed, completion, etc.)
- **Difficulty Ratings**: Weighted achievement values
- **Community Features**: Achievement sharing and leaderboards

### Level System
- **Branching Paths**: Multiple progression routes
- **Special Levels**: Boss levels with unique mechanics
- **Prestige System**: Meta-progression after level completion

### Database System
- **Cloud Sync**: Optional cloud-based achievement database
- **User Contributions**: Community achievement submissions
- **Versioning**: Database migration system for updates
