Here’s your **complete, enhanced Markdown specification** for RetroAchievements achievement formulas—including all advanced flags, syntax rules, and links to the official documentation for further reference.

---

# RetroAchievements Achievement Formula Specification (Full)

This document compiles the full syntax and semantics of the RetroAchievements condition language used to define achievement logic, including memory types, comparison operators, flag behaviors, grouping, hit counts, and more.

---

## 1. Memory Reference Types & Modifiers

RetroAchievements reads from emulator RAM using prefixes that define the memory size or format:

| Prefix              | Description                  |
| ------------------- | ---------------------------- |
| `0xH####`           | 8-bit value                  |
| `0xL####`           | 16-bit little-endian         |
| `0xX####`           | 24-bit little-endian         |
| `0xM####`           | 32-bit little-endian         |
| `0xI/J/G`           | BE (big-endian) variants     |
| `0xK####`           | BitCount (count of set bits) |
| `fF/fB/fH/fI/fM/fL` | Floating-point formats       |

Modifiers:

* `d0x...` or `d`: Delta (current − previous frame).
* `p`: Prior value (value in the previous frame).
* `b`: BCD format.
* `~`: Invert bits.

For a full breakdown, see **Condition Syntax** ([docs.retroachievements.org][1]).

---

## 2. Comparison Operators

Standard comparison syntax:

* `=` equal
* `!=` not equal
* `<`, `>`, `<=`, `>=` for relational comparisons

These are used with memory references directly, e.g.:

```
0xH0073 = 85
d0xH0073 = 75
```

Refer to **Condition Syntax** for full context ([docs.retroachievements.org][1]).

---

## 3. Condition Flags (Logical Behaviors)

Each condition can include a flag to alter behavior:

| Flag                                                                                | Meaning                                                                                      |
| ----------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `P:`                                                                                | PauseIf – pauses hit counts until condition clears ([docs.retroachievements.org][2])         |
| `R:`                                                                                | ResetIf – resets progress when true ([docs.retroachievements.org][3])                        |
| `Z:`                                                                                | ResetNextIf – resets only next condition                                                     |
| `A:`                                                                                | AddSource – arithmetic, adds value to accumulator ([docs.retroachievements.org][4])          |
| `B:`                                                                                | SubSource – subtracts value ([docs.retroachievements.org][4])                                |
| `C:`                                                                                | AddHits – adds to hit count                                                                  |
| `D:`                                                                                | SubHits – removes from hit count                                                             |
| `I:`                                                                                | AddAddress – adds memory value directly                                                      |
| `N:`                                                                                | AndNext – combines with the next condition via logical AND ([docs.retroachievements.org][5]) |
| `O:`                                                                                | OrNext – combines via OR ([docs.retroachievements.org][5])                                   |
| `M:`                                                                                | Measured – displays value in leaderboards or presence                                        |
| `G:`                                                                                | MeasuredIf – conditional measurement                                                         |
| `T:`                                                                                | Trigger – final condition triggering the unlock ([docs.retroachievements.org][6])            |
| `K:`                                                                                | Remember – stores value for recall later                                                     |
| (plus: `Recall` for retrieving remembered values) ([docs.retroachievements.org][1]) |                                                                                              |

See **Condition Syntax** for full flag list ([docs.retroachievements.org][1]).

---

## 4. Hit Counts (Condition Persistence)

* Hit count field defines how many frames a condition must remain true.
* Default is 0 → condition always true frame triggers.
* Specifying, e.g., `=5`, means condition must persist for 5 frames. Once met, it's locked true unless reset.
* ResetIf and PauseIf influence hit count behavior ([docs.retroachievements.org][7]).

---

## 5. Grouping & Alt Groups

* Conditions separated by colon `:` are evaluated left to right.
* Conditions within a group are **AND-ed** by default.
* Use `Alt Groups` to allow **OR-logic** between separate condition blocks ([docs.retroachievements.org][4]).
* `AndNext` / `OrNext` flag chains conditions under one logical operation ([docs.retroachievements.org][5]).

---

## 6. Delta vs. Prior Value

* `d0x...` → delta (current minus previous).
* `p0x...` → value in previous frame.
* Different semantics; often used together to detect transitions (e.g. counting increments) ([docs.retroachievements.org][1]).

---

## 7. Trigger Flag & Priming Logic

* `T:` flags condition(s) whose satisfaction triggers the achievement.
* When all non-Trigger conditions are met, achievement becomes **Primed** – a visual "challenge" indicator appears in-game ([docs.retroachievements.org][6]).
* PauseIf while primed hides indicator but preserves primed state.

---

## 8. Advanced Arithmetic & Address Operations

* `AddSource`, `SubSource`, and `AddAddress` allow arithmetic using memory values in logic decisions ([docs.retroachievements.org][8]).
* Great for scoring, timers, or dynamic sums.

---

## 9. Alt Groups & Complex Reset Behavior

* Use alt groups to handle multiple conditional variations.
* Useful for reset logic that must run even when core group is paused ([docs.retroachievements.org][4]).

---

## 10. Rich Presence & Leaderboard Integration

* Flags like `M:` and `G:` support **Rich Presence** (what players see about your game status) and **leaderboards** ([docs.retroachievements.org][9]).
* Rich Presence uses memory syntax to display dynamic information every few minutes ([docs.retroachievements.org][9]).

---

## 11. Additional Features & Best Practices

* **Bit-fields (`0xK####`)** for checking specific bits ([docs.retroachievements.org][10]).
* Use multiple conditions (not just one) to avoid false triggers (e.g., demo mode) ([docs.retroachievements.org][11]).
* Protect against savestates, passwords, or cheats using ResetIf/PauseIf.
* Design achievements to be fair, interesting, and not spam-heavy ([docs.retroachievements.org][12]).

---

### Sample Syntax Block (Using Full Feature Set)

```text
A:0xH0100=Value       # AddSource arithmetic
B:0xH0102=Offset      # SubSource arithmetic
N:0xH0200=1           # AndNext to chain
Z:0xH0201=0           # ResetNextIf resets next only
C:0xH0205=1 (Hit 10)  # AddHits with hit count
P:0xH0300=DemoFlag    # PauseIf demo mode
R:0xH0400>0           # ResetIf cheat code activated
T:0xH0500=255         # Trigger on final event
```

---

## Summary

This comprehensive guide covers:

* Memory sizes, formats, and modifiers
* Comparison operators
* Full set of flags and their behaviors
* Hit count mechanics
* Grouping and logic chaining (`AndNext`, `OrNext`, alt groups)
* Delta vs prior memory semantics
* Trigger logic and priming
* Arithmetic operations in condition logic
* Rich Presence & leaderboard integration
* Design best practices and anti-cheat safeguards

Each section links to the **official RetroAchievements documentation** for deeper reference—so you can be confident this spec matches the latest guidance.

Let me know if you'd like any examples (e.g. boss defeat, speedrun challenge) fully annotated with this spec!

[1]: https://docs.retroachievements.org/developer-docs/condition-syntax.html?utm_source=chatgpt.com "Condition Syntax in Achievement Development"
[2]: https://docs.retroachievements.org/developer-docs/flags/pauseif.html?utm_source=chatgpt.com "Utilizing PauseIf to Control Achievement Hit Counts"
[3]: https://docs.retroachievements.org/developer-docs/flags/resetif.html?utm_source=chatgpt.com "Mastering ResetIf to Manage Achievement Hit Counts"
[4]: https://docs.retroachievements.org/developer-docs/achievement-templates.html?utm_source=chatgpt.com "Achievement Templates for Various Scenarios"
[5]: https://docs.retroachievements.org/developer-docs/flags/andnext-ornext.html?utm_source=chatgpt.com "Mastering AndNext and OrNext for Complex Achievement ..."
[6]: https://docs.retroachievements.org/developer-docs/flags/trigger.html?utm_source=chatgpt.com "Leveraging the Trigger Flag for Achievement Indicators"
[7]: https://docs.retroachievements.org/developer-docs/hit-counts.html?utm_source=chatgpt.com "Hit Counts"
[8]: https://docs.retroachievements.org/orphaned/achievement-logic-features.html?utm_source=chatgpt.com "Achievement Logic Features"
[9]: https://docs.retroachievements.org/developer-docs/rich-presence.html?utm_source=chatgpt.com "Rich Presence"
[10]: https://docs.retroachievements.org/developer-docs/tips-and-tricks.html?utm_source=chatgpt.com "Tips and Tricks"
[11]: https://docs.retroachievements.org/developer-docs/getting-started-as-an-achievement-developer.html?utm_source=chatgpt.com "Getting Started as an Achievement Developer"
[12]: https://docs.retroachievements.org/guidelines/content/achievement-set-requirements.html?utm_source=chatgpt.com "Achievement Set Requirements"
