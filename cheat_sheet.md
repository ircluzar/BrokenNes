# RetroAchievements Achievement Formula Cheat Sheet

A comprehensive, **beginner-friendly and expert-level markdown reference** for building achievement logic on RetroAchievements: syntax, operators, functions, memory inspection, code notes, formulas, API interaction, badge/ icon creation, community process, and best practices—**synthesized from official docs, guides, code of conduct, real templates, and expert forum commentary**. Explanatory paragraphs elaborate all tables and code examples.

---

## Introduction

RetroAchievements extends classic games with a modern achievement system, integrated into emulators via a robust scripting formula language. Both newcomers and advanced developers need to master its syntax, memory manipulation logic, condition flags, alt groups, hit counts, and complex protections to create fair, creative, and robust achievements. The RetroAchievements developer ecosystem values clear structure, effective code notes, regional compatibility, creative badges, and a strong anti-cheat stance.

This cheat sheet collects, organizes, and **deeply explains all aspects necessary for successful RetroAchievement formula creation**, from key syntax and conditional operators to the art of memory hacking, badge/icon best practices, and official community release flow. Inline tables and code-paragraph pairings ensure that concepts are accessible at a glance and fully explained.

---

## Achievement Formula Core Concepts

Achievement formulas (also called "achievement logic" or "requirements") are conditions, often referencing game memory, that determine when an achievement unlocks. Achievements are monitored every frame during emulation:

- Achievements are rewarded when **all logic conditions** become true in the same frame, unless using alternative groups (explained later).
- Achievement triggers must be safe from exploits, such as cheat codes, demo mode, passwords, or save file manipulation.
- **Formulas must be tested and documented**, both for bugfixing and for fellow developers.

---

## Section Overview

- [Core Syntax and Components](#core-syntax-and-components)
- [Memory Access and Sizes](#memory-access-and-sizes)
- [Condition Flags (Logic Modifiers)](#condition-flags-logic-modifiers)
- [Operators, Comparisons, and Delta/Prior](#operators-comparisons-and-deltaprior)
- [Hit Counts and Advanced Timing](#hit-counts-and-advanced-timing)
- [Alt Groups (OR Logic) and Conditional Resets](#alt-groups-or-logic-and-conditional-resets)
- [Special Functions: PauseIf, ResetIf, AddSource, Remember](#special-functions-pauseif-resetif-addsource-remember)
- [Templates and Real-World Examples](#templates-and-real-world-examples)
- [Memory Inspector, Address Hunting, and Code Notes](#memory-inspector-address-hunting-and-code-notes)
- [PAR/Patch Code Conversion](#parpatch-code-conversion)
- [Best Practices and Anti-Cheat](#best-practices-and-anti-cheat)
- [Achievement Naming, Description, and Localization](#achievement-naming-description-and-localization)
- [Badge/Icon Creation](#badgeicon-creation)
- [Demo Mode, Cheats, and Save Protection](#demo-mode-cheats-and-save-protection)
- [Achievement Types, Progression, Win Condition, and Subsets](#achievement-types-progression-win-condition-and-subsets)
- [Community Guidelines, Review, and Release Process](#community-guidelines-review-and-release-process)
- [API Integration and Front-End](#api-integration-and-front-end)
- [References: Additional Resources](#references-additional-resources)

---

## Core Syntax and Components

At the heart of every achievement is a **list of conditions**, evaluated as a group: either all must be true (**AND**-logic in a single group), or (Core **AND** Alt1) **OR** (Core **AND** Alt2) if using alt groups.

Achievement conditions are structured as:

```txt
[Flag:] [Type] [Size] [Address/Value/Recall/Delta] [Operator] [Type] [Size] [Value/Address/Recall/Delta] (HitCount)
```

- **Flag:** Modifies the logic (e.g., `ResetIf:`, `PauseIf:`, `AddSource:`, `AndNext:`, `OrNext:`, `Remember:`)
- **Type/Size:** What kind of data and bit length to read (e.g., `Mem 8-bit` or `Delta 16-bit`)
- **Address/Value:** Memory address (in hex), or a constant value, or a reference to previous value
- **Operator:** Defines how left and right are compared (`=`, `!=`, `>`, `<`, `>=`, `<=`, math ops)
- **HitCount:** (Optional) How many times this requirement must be true

### Example

Award achievement when 16-bit value at 0xFE20 (player’s rings) is at least 15:

```txt
Mem 16-bit 0xFE20 >= Value 15
```

**Paragraph Explanation:**

This basic formula checks each frame if the rings count at memory address `0xFE20` is greater than or equal to 15. When this is true, and no other conditions are specified, the achievement will unlock. To prevent errant triggers, best practice is to add further conditions (e.g., not in demo mode, not in main menu).

---

## Memory Access and Sizes

### Supported Memory Sizes and Prefixes

| Memory Size     | Prefix    | Syntax Example         | Description                         |
|-----------------|-----------|------------------------|-------------------------------------|
| Single Bit      | 0xM-0xT   | `0xM1234`              | Bit 0 (use 0xN for Bit1, ... 0xT for Bit7) |
| Lower4          | 0xL       | `0xL1234`              | Lower nibble (bits 0-3)             |
| Upper4          | 0xU       | `0xU1234`              | Upper nibble (bits 4-7)             |
| 8-bit           | 0xH       | `0xH1234`              | Standard byte                       |
| 16-bit          | (none)    | `0x1234`               | Two bytes, little endian            |
| 24-bit          | 0xW       | `0xW1234`              | Three bytes, little endian          |
| 32-bit          | 0xX       | `0xX1234`              | Four bytes, little endian           |
| 16-bit BE       | 0xI       | `0xI1234`              | Big-endian                          |
| 24-bit BE       | 0xJ       | `0xJ1234`              | Big-endian                          |
| 32-bit BE       | 0xG       | `0xG1234`              | Big-endian                          |
| BitCount        | 0xK       | `0xK1234`              | # of set bits in 8 bits at 0x1234   |

**Explanation:**

Memory can be accessed as bytes, words, or even single bits, crucial for reading flags or counters accurately. Use the correct prefix for the intended access: e.g., reading health as a 16-bit value is `0x1234`, not `0xH1234`. Big-endian versions (`0xI`, `0xJ`, `0xG`) are used on systems like the GameCube. Bit fields are common for items, event flags, or boolean states.

---

### Modifiers on Operands

Certain **modifiers** let you reference time-relative data or perform specific math on-the-fly.

| Modifier   | Syntax               | Meaning                           |
|------------|----------------------|-----------------------------------|
| Delta      | `d0xH1234`           | Value in previous frame           |
| Prior      | `p0xH1234`           | Value two frames ago              |
| BCD        | `b0xH1234`           | Value in Binary-Coded Decimal     |
| Invert     | `~0xH1234`           | Invert all bits (bitwise NOT)     |
| Recall     | `{recall}`           | Last value remembered in a group  |

**Paragraph:**

The Delta operator is especially important—it enables you to detect **changes between frames**, such as when a counter increments (e.g., collecting a ring). Prior can detect more complex transitions, such as triple-frame events or debouncing counters.

---

## Condition Flags (Logic Modifiers)

Flags modify both the logic and evaluation order of achievement formulas. They may reset progress, pause comparisons, or chain conditions.

| Flag         | Syntax       | Effect                                                                           |
|--------------|--------------|----------------------------------------------------------------------------------|
| ResetIf      | `R:`         | If true, resets all hit counts and progress in all groups                        |
| PauseIf      | `P:`         | If true, pauses this group (no hit counts increase, no resets processed)         |
| ResetNextIf  | `Z:`         | If true, resets next condition's hit count only                                  |
| AddSource    | `A:`         | Adds value to current source in comparison                                       |
| SubSource    | `B:`         | Subtracts value from current source in comparison                                |
| AddHits      | `C:`         | Add to hit count if true                                                         |
| SubHits      | `D:`         | Subtract from hit count if true                                                  |
| AddAddress   | `I:`         | Add to the memory address being compared                                         |
| AndNext      | `N:`         | Chains this condition to the next (logical AND)                                  |
| OrNext       | `O:`         | Chains this condition to the next as logical OR                                  |
| Measured     | `M:`, `G:`   | For measured achievements, like leaderboards/score tracking                      |
| Trigger      | `T:`         | Shows the challenge icon on HUD when active (good for "in-progress" display)     |
| Remember     | `K:`         | Saves current value for later Recall in this group                               |

**Paragraph Discussion:**

Flags are the backbone of sophisticated achievement logic. **ResetIf** is essential to prevent partial progress from accidentally awarding the achievement when failure occurs (e.g., if the player dies). **PauseIf** is key for handling conditions you must ignore during pauses, menus, or special scenarios (e.g., demo mode, cutscenes). **AndNext/OrNext** allow complex logical chains. Remember/Recall unlock advanced calculated logic and pointer handling.

---

## Operators, Comparisons, and Delta/Prior

Achievement conditions use standard **comparisons**, but also math for value tracking.

| Operator              | Usage           | Example                        | Meaning                        |
|-----------------------|-----------------|--------------------------------|--------------------------------|
| = (equals)            | `=`             | `0xFE20 = 15`                  | Memory = Value                 |
| != (not equals)       | `!=`            | `0x10 != 7`                    | Memory ≠ Value                 |
| > (greater)           | `>`             | `0x1234 > 20`                  | Memory > Value                 |
| < (less)              | `<`             | `0xFB = Delta 0xFB`            | Memory this frame == prior     |
| >=, <=                | `>=`, `<=`      | `0xFE20 >= 10`                 | Standard math comparisons      |
| - (minus)             | `-`             | `Mem 0x1004 - Delta 0x1004`    | Difference between frames      |
| +, *, /, %, ^         | `+`, `*`, `/`   | `Recall * 3`                   | Additional math for Recall     |

**Delta Example:**

If you want to count "every time the player’s rings increase," use:

```
Mem 0xFE20 > Delta 0xFE20 (HitCount: N)
```

**Paragraph:**

The inclusion of **Delta** and **Prior** operators is critical for tracking events that occur between frames, such as a counter incrementing, a health bar filling, or a player entering a new level. With hitcounts, these allow you to build logic like “defeat 10 enemies,” “collect all coins,” or “progress without damage for N frames.” Math operators extend logic for complex behaviors.

---

## Hit Counts and Advanced Timing

The **hit count** field (rightmost in a condition) instructs that a condition must be true for N frames (or events) before advancing the logic.

### Key Concepts

| Field  | Example                     | Description                             |
|--------|-----------------------------|-----------------------------------------|
| (N)    | `Mem 0xFE20 > Delta 0xFE20 (10)` | Needs 10 successful events     |
| None   | `Mem 0xFE20 = 5`            | Default, must be true for 1 frame      |

- Once hit count is satisfied, the condition is locked True, unless a ResetIf occurs.
- Use with Delta comparisons for counting events.

**Timer Example:**

"Maintain speed >= 300 for 10 seconds at 60 FPS": `Mem 0x0055a >= 300 (600)`—the player must be moving fast for 600 frames.

**Explanation:**

Hit counts are not just for repeating events, but also for enforcing durations. Use ResetIf to clear the hit count if an invalid state occurs (e.g., player slows down, pauses the game).

---

## Alt Groups (OR Logic) and Conditional Resets

### What Are Alt Groups?

- **Alt Groups** provide **OR logic**: for achievement to trigger, all conditions in Core group **and** all in **any one** of the Alt groups must be true.
- You can create logic like “while on this stage, look up OR crouch.”

**Syntax Example Table:**

| Group   | Condition              |
|---------|------------------------|
| Core    | `0x18 = 5` (on stage)  |
| Alt 1   | `0xBC = 1` (look up)   |
| Alt 2   | `0xBC = 2` (crouch)    |

One Alt group being **completely true**, alongside the core group, is sufficient for awarding the achievement.

**Conditional Resets:**

Place ResetIf/PauseIf in Alts for conditional effects (e.g., only allow resets in specific regions or under special circumstances).

**Alt vs. Core:**

- **Core = AND for all conditions**
- **Alt = OR among groups (but AND within group)**

**Paragraph:**

Alt groups unlock the ability to make more flexible expressions; for example, you can award an achievement if a player defeats a boss as **either** Character A or Character B, or regains health by one of several mechanisms. Conditional resets within Alts also solve edge cases where players might otherwise erroneously trigger or lose achievements.

---

## Special Functions: PauseIf, ResetIf, AddSource, Remember

### PauseIf

- Pauses achievement progression if true (does not reset, just suspends).
- Essential for handling game states like pause menu, demo mode, or cutscenes.
- Only resumes when condition is untrue.
- If PauseIf is true, **no further conditions in group are evaluated.**

### ResetIf

- Resets all hitcounts and achieved progress whenever true.
- Prevents partial progress from triggering achievements after failure (e.g., after player death or menu exploit).

### AddSource / SubSource

- Used for calculating running sums or differences within a condition chain.
- E.g., count the sum of collectibles, or check if multiple conditions/values jointly meet a goal.

### Remember/Recall

- Store a value during one part of logic to use again later.
- Allows for advanced calculations, pointer mathematics, or multi-step conditions.

**Example Table:**

| Flag      | Use Case                                    |
|-----------|---------------------------------------------|
| PauseIf   | While menu/paused/demomode                  |
| ResetIf   | If player lost a life or left stage         |
| AddHits   | Add to hit count only if complex condition  |
| Remember  | Store calculated difference for later check |
| Recall    | Reference value stored with Remember        |

**Paragraph:**

**PauseIf** and **ResetIf** are vital for robust and fair achievement logic, preventing progress from becoming invalid or achievements popping in error due to glitches, exploits, demo actions, or user menu manipulation. By carefully associating these flags, you can prevent nearly all undesired edge-case unlocks. **Remember/Recall** empower developers to craft high-performance, non-redundant logic and repeated calculations.

---

## Templates and Real-World Examples

Below are quick-reference snippets for common achievement scenarios, **always explained in the following paragraphs for clarity**.

### 1. Collect Item N Times (with death reset)

```txt
Mem 0xCOUNT > Delta 0xCOUNT (N)
ResetIf Mem 0xLIVES < Delta 0xLIVES
```
*Counts collection increments, resets on life loss.*

### 2. Finish Level N (on transition)

```txt
Delta 0xLEVEL = N
Mem 0xLEVEL = N+1
```
*Award only on correct frame of transition to next level.*

### 3. Speedrun/Timer Achievement

```txt
Mem 0xTIME > T
Trigger Mem 0xLEVEL = N+1
PauseIf Mem 0xLEVEL = N (T*FRAMERATE)
```
*Complete level before timer fails; challenge icon will show if active.*

### 4. No-Damage/No-Death

```txt
AndNext Mem 0xLEVEL = N
Mem 0xLVL_STATE = LVL_N_INTRO (1)
Delta 0xLEVEL = N
Trigger Mem 0xLEVEL = N+1
ResetIf Mem 0xLIVES < Delta 0xLIVES
```
*Fails if lives drop; only valid if in correct level/init state.*

**Paragraph Explanation:**

Each template references one or more **memory addresses**, typically discovered via the Memory Inspector tool (see below). **ResetIf** ensures that dying, quitting, or otherwise failing a run resets the attempt—essential for "in one go" challenges. Timer-based templates use hitcounts set to frame counts (e.g., 600 hits = 10 seconds at 60 FPS). These logic blocks can be stacked or adjusted for greater specificity as needed—always driven by actual memory behavior, which can differ drastically between games.

---

## Memory Inspector, Address Hunting, and Code Notes

To write meaningful achievement logic, **finding the right memory addresses is fundamental**.

### Main Steps with the Memory Inspector:

1. **Start the game and open the Memory Inspector.**
2. Begin a new search (often 8-bit to start).
3. Use in-game actions to change the target value (e.g., collect a ring, lose a life).
4. Use **filters**:
    - Greater than previous: Characters gained an item, rings, coins, etc.
    - Less than previous: Lost health, lost a life.
    - Equal: State did not change (helpful for filtering by invariance).
5. Narrow down results; use bookmarks and notes.
6. Save code notes with details and value lists for future maintenance.

### Code Notes Best Practices

- Be **explicit and clear**: address, size, all possible values, and context.
- Mark single bits for event flags so future devs understand.
- Use consistent formatting: `"[8-bit] #Lives: 0x00-9 (actual used: 1-4)"`.
- Always **specify values in hex** unless game uses decimal.

### Example Table for Notation

| Address  | Description           | Size      | Values/Notes               |
|----------|----------------------|-----------|----------------------------|
| 0xFE20   | Player rings         | 16-bit    | Main Counter               |
| 0x9002   | Screen mode          | 8-bit     | 0x1 Title, 0x2 Intro, ...  |
| 0xB400   | Has Powerup X        | Bit4      | 1=true, 0=false            |

**Paragraph:**

The Memory Inspector is essentially the "RAM hacking" heart of RetroAchievements development. Carefully filtering for changes after each gameplay event isolates key addresses. Saving robust, well-written notes ensures the achievement logic is maintainable—not just by you, but by anyone in the community who inherits your code or needs to debug odd triggers.

Remember that some systems—like GBA—have remapping between raw codes and RA’s address space, so always validate in the Inspector before using addresses from cheat sites.

---

## PAR/Patch Code Conversion

- **PAR (Pro Action Replay) or CodeBreaker codes** are useful as starting points for address hunting, but must often be adapted.
- SNES: Strip control bytes, verify offset, and test in Memory Inspector.
- GBA: Internal RAM at 0x03000000, WRAM at 0x02000000—Memory Inspector may remap these.

**Example:**  
PAR code `FFFE20:00C8` (Sonic 1):  
- Address = `0xFE20`, Value = `0x00C8` (200 rings)
- Use as `Mem 16-bit 0xFE20 = Value 200` in logic.

**Paragraph:**

While codes from cheat devices or hacking sites are a valuable lead, actual RA integration demands empirical verification. Game memory may shift between sessions or differ in "banked" memory situations. Always use the Inspector to lock down the address and data layout in the current emulator and game version.

---

## Best Practices and Anti-Cheat

**Protect Against:**

- **Demo mode:** Achievements must not trigger during attract mode or AI play.
- **Password/Load Exploits:** Prevent unlocking for "earned items" when loading a save or password that provides direct access.
- **Cheat Codes:** Require additional state checks—e.g., ensure Stage Select code not active.
- **Single-Condition Achievements:** Never rely on a single check; always pile up verifying conditions.

**Table:**

| Protection    | Mechanism                       | Example                                 |
|---------------|--------------------------------|-----------------------------------------|
| Demo Mode     | Add `Mem 0xXX = InGameValue`   | Demo flag, or not Title/MainMenu state  |
| Password      | Check if progress only increments live | Check for actual collection increment in right context |
| Cheating      | PauseIf or Alt group conditions| Verification against cheat activation   |

**Paragraph:**

Most achievement bugs and abuses stem from under-protecting logic. Coding for the *exact* in-game event, not merely for the end state, is essential. Use multi-frame and multi-condition logic, tied tightly to "player in level, not paused, not demo mode" and so on, to ensure integrity. Stacking **ResetIf** with **PauseIf** ensures stable, fair achievement evaluation.

---

## Achievement Naming, Description, and Localization

**Naming Standards:**

- Titles and descriptions *must* be in English, unless using necessary in-game or thematic foreign language.
- Creative, themed, or clever titles are encouraged (e.g., alliteration, puns, references).
- Descriptions: concise, explicit, spoiler-free if possible. Avoid single-line "beat level 1" repetition; use thematic grouping.

**Table:**

| Field      | Convention         | Example                          |
|------------|--------------------|----------------------------------|
| Title      | Chicago style/cap  | "So Long, Sanson"                |
| Description| First letter cap,  | "Defeat Sanson without damage."  |

**Paragraph:**

A vibrant, creative title list makes sets memorable and more than just a rote checklist. Emphasize progression, challenge, and collection, but avoid spammy, repetitive achievements (e.g., "Reach Level 1," "Reach Level 2," ...). Localization for code notes is always English, to facilitate international developer handover.

---

## Badge/Icon Creation

**Standards:**

- Badges: 64x64 PNG, no transparency. Should visually relate to achievement.
- Icons: 96x96 PNG, official art, game-representative.

| Asset  | Format | Best Practice                    |
|--------|--------|----------------------------------|
| Badge  | 64x64  | Show in-game item, boss, area    |
| Icon   | 96x96  | Use official art, clean at 32x32 |

**Paragraph:**

Clear, beautiful badges and icons greatly deepen the player experience, making a set feel polished and rewarding. Avoid generic, blurry, or duplicate icons—iterate designs until they’re crisp and symbolic, even when downsampled. All assets must follow RetroAchievements visual guidelines and, in the case of icons, strict "official art only" policy (except for hacks or when developer-created art is approved by the team).

---

## Demo Mode, Cheats, and Save Protection

- Always add conditions to ensure **achievements don’t trigger during demo/attract mode, cheat code usage, or via passwords**.
- Use PauseIf to suppress logic if demo mode, menu, or cheat code is detected; combine with Alt group conditional resets for fine control.
- For collectibles: only award if the collection occurs in the correct context, not on load or via password.

**Paragraph:**

Without protections, achievements can be unlocked simply by loading a password or by the AI in demo loops. Always hunt for a demo/cheat flag or scene/state variable, and tie logic to it. Sometimes, you may need research and experimentation to find such flags (e.g., filtering across various menus and demo screens).

---

## Achievement Types, Progression, Win Condition, and Subsets

**Guidelines:**

- **Progression**: Steady, mandatory in-game progress. E.g., "Defeat first boss," "Complete world 3."
- **Win Condition**: End-of-game, story-resolving achievements.
- **Subsets**: Extreme challenges, alternative runs, multiplayer, or bonus content (see Subset Guide).

**Table:**

| Type        | Assigned By        | Used For                  | Example                       |
|-------------|--------------------|---------------------------|-------------------------------|
| Progression | Dev Panel on RA    | Main, mandatory events    | Completion of key levels      |
| Win         | Dev Panel on RA    | Game endings, final boss  | Defeat last boss, see credits |
| Subset      | By hack/patch      | Challenge, ex-modes       | Full damageless, rare drop    |

- Use the Achievement Types in the web developer panel, **not through the emulator**, for proper set labeling (as of 2025).
- Subsets should be reserved for content not appropriate for main sets (e.g., full-no-damage runs, multiplayer co-op, etc.).

**Paragraph:**

Typings are crucial not only for community clarity (marking games as "beaten" or "mastered") but also for site functionality—progress tracking, stats, and event participation. They must be consistently and cleanly applied, and are subject to community revision and moderation.

---

## Community Guidelines, Review, and Release Process

- **Test achievements** locally.
- Submit as **Unofficial Achievements**: Peer-reviewed, not yet public.
- Promote to **Core** when finalized, unless deemed bonus or subset content.
- Achievements violating guidelines or causing bugs/complaints are flagged and must be fixed/updated.
- Follow the **Developer Code of Conduct**: collaboration, transparency, and maintenance are required.

**Paragraph:**

A robust, transparent release process ensures that sets are fair, bug-free, and align with community standards. The peer review system balances innovation and stability, while the open spirit of RetroAchievements allows all developers (and junior devs) to submit, revise, and help maintain sets over time.

---

## API Integration and Front-End

- The **RetroAchievements API** offers endpoints for retrieving user, game, and achievement data, leaderboards, progress, and more (JSON format).
- Libraries exist for NodeJS, Kotlin, and other stacks.
- Achievements, badges, and metadata can be pulled for integration into custom overlays, dashboards, or stream widgets.
- All API use requires a user’s API key (obtained on the user profile).

**Table:**

| Functionality      | API Doc Reference                              |
|--------------------|-----------------------------------------------|
| User Progress      | `/User/UserProgress`                           |
| Game Info          | `/Game/GameInfo`                               |
| Recent Achievements| `/User/UserRecentAchievements`                 |
| Leaderboards       | `/Game/Leaderboards`, `/Leaderboard/Entries`   |
| Badge/Image Data   | URLs like `https://i.retroachievements.org/Badge/XXXXX.png` |

**Paragraph:**

Integrating RetroAchievements into front-end overlays or stat trackers is straightforward thanks to the feature-rich, well-documented web API. Most community-developed overlays, such as those used by streamers, use these endpoints for real-time updates of achievement progress and badges.

---

## References: Additional Resources

- [RetroAchievements Docs Home](https://docs.retroachievements.org/)
- [Achievement Templates](https://docs.retroachievements.org/developer-docs/achievement-templates.html)
- [Progression and Win Condition Guidelines](https://docs.retroachievements.org/guidelines/content/progression-and-win-condition-guidelines.html)
- [Getting Started as an Achievement Developer](https://docs.retroachievements.org/developer-docs/getting-started-as-an-achievement-developer.html)
- [Condition Syntax](https://docs.retroachievements.org/developer-docs/condition-syntax.html)
- [Memory Inspector Overview](https://docs.retroachievements.org/developer-docs/memory-inspector.html)
- [Code Notes](https://docs.retroachievements.org/guidelines/content/code-notes.html)
- [Badge and Icon Guidelines](https://docs.retroachievements.org/guidelines/content/badge-and-icon-guidelines.html)
- [API Docs](https://api-docs.retroachievements.org/)
- [Forum: Badge and Icon Creation](https://retroachievements.org/viewtopic.php?t=141)
- [Subset Guidelines](https://docs.retroachievements.org/guidelines/content/subsets.html)
- [Writing Policy](https://docs.retroachievements.org/guidelines/content/writing-policy.html)

**Paragraph:**

The official documentation (linked above) is continuously updated and extends well beyond this cheat sheet, offering deep dives into badge creation, subset management, API deep integrations, and more. For cutting-edge or game-specific techniques, consult the developer forums, Discord, or community-maintained wikis.  

---

## Closing Notes and Further Learning

RetroAchievements formula creation is both a technical and creative pursuit; mastery is achieved through practice, collaborative feedback, and careful study of both memory structures and community standards. Reference and build upon this cheat sheet, but always tailor your logic to **the unique memory layout, behavior, and quirks of the specific game at hand**. When in doubt, consult the official documentation, reach out to the developer community for guidance, and ensure your logic is clear, robust, and enjoyable for all players.

**Happy Achievement Crafting!**
Great! I’m putting together a comprehensive Markdown cheat sheet that covers everything you need to know about creating retroachievement formulas—from syntax and functions to best practices and examples. This will take me a little while, so feel free to step away and check back later. The cheat sheet will be saved right here in our conversation.
