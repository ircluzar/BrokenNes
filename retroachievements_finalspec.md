# RetroAchievements Achievement Formula Language — Final Specification (Consolidated, 2025-08)

This document consolidates and reconciles the three provided references into a single, practical specification for authoring, parsing, and evaluating RetroAchievements (RA) achievement logic. Conflicts from earlier drafts are resolved using the clarification report (Aug 2025). Where historic/legacy behavior still exists, it is explicitly noted.

- Authoring view: how to write correct and robust logic.
- Specification view: memory types, flags, operators, groups, hit counts, evaluation model.
- Implementation view: parsing and runtime evaluation guidance.

---

## Contents

- Concepts and Evaluation Model
- Memory References and Sizes (incl. Floating-Point Formats)
- Operand Modifiers (Delta/Prior/BCD/Invert/Recall)
- Operators and Arithmetic
- Condition Flags (PauseIf, ResetIf, AndNext, OrNext, Measured, Trigger, etc.)
- Hit Counts and Duration Semantics
- Groups and Alt Groups (OR-logic)
- Trigger and Measured Semantics (M:, G:, Q:)
- Best Practices and Anti-Cheat Protections
- Authoring Templates and Examples
- Parsing and Serialization Notes
- Execution/Runtime Guidance
- API, Rich Presence, and Leaderboards
- Testing and Validation
- References
- Resolved Conflicts Summary

---

## Concepts and Evaluation Model

- An achievement is a collection of conditions evaluated every frame.
- Within a single group, all conditions must be true (logical AND).
- Alt groups provide OR-logic at group level: Core AND (Alt1 OR Alt2 OR …).
- Achievements unlock when required group(s) become true, subject to flags and hit counts.
- Include protections for demo mode, cheat codes, passwords, menus, and save/load exploits.

---

## Memory References and Sizes

Access prefix determines how memory is read. Use the correct size and endianness for the target system and value.

| Size/Type       | Prefix      | Example       | Description                                             |
|-----------------|-------------|---------------|---------------------------------------------------------|
| Single bit 0..7 | 0xM..0xT    | 0xM1234       | Bit 0..7 of byte at address                             |
| Lower nibble    | 0xL         | 0xL1234       | Bits 0–3 of byte                                        |
| Upper nibble    | 0xU         | 0xU1234       | Bits 4–7 of byte                                        |
| 8-bit           | 0xH         | 0xH1234       | Unsigned byte                                           |
| 16-bit (LE)     | none        | 0x1234        | Unsigned 16-bit value (little endian)                   |
| 24-bit (LE)     | 0xW         | 0xW1234       | Unsigned 24-bit value                                   |
| 32-bit (LE)     | 0xX         | 0xX1234       | Unsigned 32-bit value                                   |
| 16-bit (BE)     | 0xI         | 0xI1234       | Big-endian 16-bit                                       |
| 24-bit (BE)     | 0xJ         | 0xJ1234       | Big-endian 24-bit                                       |
| 32-bit (BE)     | 0xG         | 0xG1234       | Big-endian 32-bit                                       |
| BitCount(8)     | 0xK         | 0xK1234       | Number of set bits in the byte at address               |

Notes:
- Use bit prefixes (0xM..0xT) for boolean flags and item bitfields.
- For platforms with mixed endianness, verify in the Memory Inspector.

### Floating-Point Formats (Supported in Achievements, RP, Leaderboards)

| Prefix | Name         | Endianness | Bits | Description                         |
|--------|--------------|------------|------|-------------------------------------|
| fF     | Float        | LE         | 32   | IEEE754 float (LE)                  |
| fB     | Float        | BE         | 32   | IEEE754 float (BE)                  |
| fH     | Double32     | LE         | 32   | Platform-specific 32-bit “double”   |
| fI     | Double32     | BE         | 32   | Platform-specific 32-bit “double”   |
| fM     | MBF32        | platform   | 32   | Microsoft Binary Float (32-bit)     |
| fL     | MBF32 (LE)   | LE         | 32   | MBF32 Little-Endian                 |

Examples:
- Float compare: `fF01234 = 1.0`
- Measured float: `M:fB080000 < 0.5`

Caveat: Only use where the game actually stores floats. Validate with memory inspector.

---

## Operand Modifiers (Delta, Prior, BCD, Invert, Recall)

Apply to operands to access time-relative values or transform data on read.

| Modifier | Syntax example   | Meaning                                          |
|----------|------------------|--------------------------------------------------|
| Delta    | d0xH1234         | Value from previous frame                        |
| Prior    | p0xH1234         | Value from two frames ago                        |
| BCD      | b0xH1234         | Interpret value as Binary-Coded Decimal          |
| Invert   | ~0xH1234         | Bitwise NOT of the value                         |
| Recall   | Recall           | Retrieve the last Remember’ed value (see K:)     |

- Delta/Prior enable transitions and per-frame change detection.
- Recall pairs with Remember (K:) to reuse values earlier in the group.

---

## Operators and Arithmetic

- Comparisons: `=`, `!=`, `<`, `<=`, `>`, `>=`
- Arithmetic in value/source chains: `+`, `-`, `*`, `/`, `%`
- Bit checks typically use 0xM..0xT or masks via nibble/bitcount

Explicit note:
- The exponent operator `^` is not supported in achievement conditions. Use multiplication.

---

## Condition Flags (Logic Modifiers)

Flags appear as a prefix and alter evaluation semantics.

| Flag | Name         | Effect (summary)                                                                 |
|------|--------------|----------------------------------------------------------------------------------|
| P:   | PauseIf      | When true, pauses the group: hit counts don’t advance; resets don’t fire         |
| R:   | ResetIf      | When true, resets all hit counts/progress of the achievement                      |
| Z:   | ResetNextIf  | When true, resets the next condition’s hit count only                             |
| A:   | AddSource    | Adds operand to a running source accumulator for a comparison                     |
| B:   | SubSource    | Subtracts operand from the running source                                        |
| C:   | AddHits      | Adds to the hit count when the condition is true                                 |
| D:   | SubHits      | Subtracts from the hit count when the condition is true                          |
| I:   | AddAddress   | Adds value to the address used for reading (pointer/offset-like)                 |
| N:   | AndNext      | Chains this condition to the next with logical AND (subgrouping)                 |
| O:   | OrNext       | Chains this condition to the next with logical OR (subgrouping)                  |
| M:   | Measured     | Marks value tracked for progress bar (raw value)                                 |
| G:   | Measured %   | Measured progress shown as percentage                                            |
| Q:   | MeasuredIf   | Gates Measured/Measured% updates; if any Q: is false, measured value is zeroed   |
| T:   | Trigger      | Marks condition whose satisfaction fires the unlock when primed                  |
| K:   | Remember     | Saves current value for later `Recall` within the group                          |

Guidance:
- ResetIf for “in-one-go” challenges (death/menu resets).
- PauseIf to ignore menus/demos/cutscenes.
- AndNext/OrNext to form micro-groups (A&B or A|B) without extra groups.
- AddSource/SubSource/AddAddress for computed comparisons and pointer-like addressing.

---

## Hit Counts and Duration

Attach a hit count `(N)` to require a condition be true N frames/events before it locks true.

- Event count (e.g., ring increments N times):
````text
0xFE20 > d0xFE20 (N)
`````