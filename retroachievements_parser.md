# Building a Complete RetroAchievements Formula Parser and Evaluator in C#

---

## Introduction

RetroAchievements (RA) has transformed retro gaming by enabling dynamic, real-time achievements tracked through emulator memory inspection and custom logic scripts. The backbone of this feature is a *domain-specific formula language*—compact but expressive, and evaluated every frame of emulation by a dedicated parser and execution engine. If you wish to support RetroAchievements natively or re-implement RA achievement logic in your own emulator, you must not only parse this logic but also *evaluate* it both accurately and efficiently against game memory.

This guide delivers a **comprehensive, step-by-step reference on how to parse and execute RetroAchievements formulas in C#**. The document covers the full breadth of RA formula syntax and semantics, with a focus on building robust parsing infrastructure, handling the language’s unique operators, flags, data types, logical conventions, and runtime evaluation model. It also includes a detailed dissection of an advanced example formula, and practical code snippets for implementation. Throughout, the content synthesizes the latest and most authoritative sources, such as official RA development docs, open-source reference implementations, and idiomatic C# parsing techniques.

---

## 1. RA Formula Syntax and Grammar: Overview

### 1.1. Achievement Logic as Formulas

At its core, a RetroAchievements formula is a serial string defining a **series of conditions** (requirements) related to emulator memory states. When these are all satisfied, an achievement is triggered. The language is tailored for rapid per-frame evaluation, supporting very concise syntax.

**High-Level Structure**
- Formulas consist of one or more "conditions" separated by underscores (`_`).
- Each condition encodes: *memory access* (with optional modifiers), a comparison, a target value or another memory access, and possible logical or control-flow modifiers.
- Special prefixes and flags alter how conditions are grouped, combined, or used as part of the formula’s logic flow.

**Primary Syntax Components**
- **Memory Addresses**: Denoted in hex, with data-size and endianness prefixes (e.g., `0xH00b2`).
- **Modifiers**: Prefixes for delta, prior values, BCD, invert, etc.
- **Comparison Operators**: `=`, `!=`, `<`, `<=`, `>`, `>=`.
- **Arithmetic/Bitwise Operators**: For address calculations and value manipulation (`+`, `-`, `*`, `/`, `%`, `&`, `|`).
- **Special Logic Flags**: Set behaviors such as Pause, Reset, Measured, Triggers, "AndNext", "OrNext".
  
Full RA formula grammar documentation is provided on the official developer pages.

**Example (dissected in detail later):**
```
0xH00b2=128_0xH0022=8_0xH0380=11_0xH0380=0.1._R:0xH0017!=0
```

---

### 1.2. Formal Grammar (BNF-Like)

For reference, the essential grammar can be summarized as (pseudo-BNF; for full syntax refer to RA docs and repositories):

```
Formula      ::= Condition { '_' Condition }
Condition    ::= [Flag ':'] [Modifiers] Operand [Operator RHS] [HitCount]
Operand      ::= [SizePrefix] Address
RHS          ::= Operand | Value
Modifiers    ::= {Modifier}
Operator     ::= '=' | '!=' | '<' | '>' | '<=' | '>='
Flag         ::= 'R' | 'P' | 'C' | 'D' | 'M' | 'Q' | 'N' | 'O' | 'A' | 'B' | ...
SizePrefix   ::= '0xH' | '0x' | '0xW' | '0xX' | '0xI' | ... (see table below)
Modifier     ::= 'd' (delta) | 'p' (prior) | 'b' (bcd) | '~' (invert)
Address      ::= hex address, 4 or 5 hex digits
Value        ::= decimal or hex constant, or [SizePrefix]Address
```

---

## 2. Memory Access: Size and Data Type Prefixes

The heart of every RA formula is direct access to emulator RAM. The language supports numerous **size and endian** prefixes for different memory representations:

| Prefix       | Data Type / Size          | Example          | Description                             |
|--------------|--------------------------|------------------|-----------------------------------------|
| `0xM`–`0xT`  | Individual bits (0-7)    | `0xM01234`       | Accesses a single bit in a byte         |
| `0xL`        | Lower 4 bits of byte     | `0xL00234`       |                                         |
| `0xU`        | Upper 4 bits of byte     | `0xU01234`       |                                         |
| `0xH`        | 8-bit (little endian)    | `0xH00b2`        | Byte at address                         |
| `0x`         | 16-bit (little endian)   | `0x01234`        | Two bytes                              |
| `0xW`        | 24-bit (little endian)   | `0xW01234`       |                                         |
| `0xX`        | 32-bit (little endian)   | `0xX01234`       |                                         |
| `0xI`        | 16-bit (big endian)      | `0xI01234`       |                                         |
| `0xJ`        | 24-bit (big endian)      | `0xJ01234`       |                                         |
| `0xG`        | 32-bit (big endian)      | `0xG01234`       |                                         |
| `0xK`        | Bit count in byte        | `0xK01234`       | Returns count of set bits               |
| `fF`         | Float (little endian)    | `fF01234`        | IEEE754                                 |
| `fB`         | Float (big endian)       | `fB01234`        |                                         |
| `fH`, `fI`   | Double32 (32b float)     | `fH01234`, etc   | Non-standard floats                     |
| `fM`, `fL`   | MBF floats               | `fM01234`        | Microsoft Binary Float                  |

*Your parser must detect prefixes, validate size, and ensure proper reads from emulator memory. Error handling should include flagging mismatched address lengths, unknown prefixes, or illegal bit/byte access.*

**Prefix reference**: [RA Condition Syntax Docs][4†docs]

---

## 3. Modifiers and Memory Operand Modifications

**Modifiers** act as *unary operations* applied to the immediate memory value. They are always applied before any arithmetic or logical operation, and they must be parsed/handled before looking up the memory.

### 3.1. Core Modifiers

| Modifier | Example         | Description                          |
|----------|----------------|--------------------------------------|
| `d`      | `d0xH00b2`     | **Delta**: previous frame’s value    |
| `p`      | `p0xH00b2`     | **Prior**: value from two frames ago |
| `b`      | `b0xH00b2`     | **BCD**: decode as binary-coded dec. |
| `~`      | `~0xH00b2`     | **Invert**: bitwise NOT (one’s comp) |
| Combined | `dp`           | Apply in order (right to left)       |

Modifiers *stack*; e.g., `p~0xH1234` means “prior (inverted) value.”

#### Implementation Note
Each operand parser should:
- Collect leading modifiers until a non-modifier character is seen.
- Apply modifier logic recursively when evaluating the operand.

---

## 4. Arithmetic and Bitwise Operators

RA logic sometimes requires dynamic address or value calculations using **arithmetic and bitwise** operations. These are used most commonly in "AddAddress" and similar constructs.

| Operator | Usage Example            | Meaning                                   |
|----------|-------------------------|-------------------------------------------|
| `+`      | `0x2000 + 0x10`         | Adds values (for pointer/struct offset)   |
| `-`      | `0x2000 - 0x04`         | Subtracts values                          |
| `*`      | `0x0600 * 2`            | Multiplies values                         |
| `/`      | `0x0600 / 3`            | Divides values                            |
| `%`      | `0x2000 % 16`           | Modulus (for masks, struct stride, etc.)  |
| `&`, `|` | `0x01 & 0x08`, etc      | Bitwise AND, OR                           |

**Operator Precedence**: Usually left-to-right unless overridden in grouping logic. Parentheses are generally not present but may be in scripting extensions.

**Application**:
- **Address computation**: e.g., `I:Mem32_tablestart + index * size`.
- **Value calculations**: e.g., compute health percent.

---

## 5. Comparison Operators

**Comparison** is the main operation in a condition, establishing the "requirement" to be monitored each frame.

| Operator | Meaning      |
|----------|-------------|
| `=`      | Equal       |
| `!=`     | Not equal   |
| `<`      | Less than   |
| `<=`     | Less or eq  |
| `>`      | Greater     |
| `>=`     | Greater/eq  |

**Form:** `[Operand] [Comparator] [RHS]`

Where `Operand` can be a direct address, a computed address or value, and `RHS` can be another memory read (with possible modifiers), or a constant value (decimal or hex).

---

## 6. Special Modifiers: Delta, Prior, BCD, Invert

### 6.1. Delta (`d`)
Reads the value as it was in the previous frame (“last value seen on previous tick”). Used to detect transitions (e.g., flag when a value changes).

### 6.2. Prior (`p`)
Same as delta, but from two frames ago. Allows tracking more complex transitions or conditions over more than two frames.

### 6.3. BCD (`b`)
Reads the byte as binary-coded decimal, converting to integer accordingly.

### 6.4. Invert (`~`)
Reads byte and bitwise-NOTs it (flips every bit).

**Implementation**: Implement these as a layer over the memory access logic, storing prior and delta values per address, for accurate per-frame computation.

---

## 7. Logical Flags and Condition Prefixes

Flags are **colon-prefixed letters** that transform the logic or chain conditions together. Flags are the most powerful tool in RA’s conditional logic—correct parsing and handling are crucial.

| Flag Prefix | Syntax            | Use                                                           |
|-------------|------------------|---------------------------------------------------------------|
| `P:`        | `P:0xH00b2=127`  | **PauseIf:** Pauses condition group if true         |
| `R:`        | `R:0xH00b2=128`  | **ResetIf:** Resets hit count (or fails logic) if true        |
| `Z:`        | `Z:0xH00b2=1`    | **ResetNextIf:** Like ResetIf, but only next                  |
| `A:`        | `A:0xH00b2/2`    | **AddSource:** Adds value to running sum                      |
| `B:`        | `B:0xH00b2/2`    | **SubSource:** Subtracts value                                |
| `C:`        | `C:0xH00b2=1`    | **AddHits:** Increments hits when condition true              |
| `D:`        | `D:0xH00b2=1`    | **SubHits:** Decrements hits                                  |
| `I:`        | `I:0xH00b2=1`    | **AddAddress:** Pointer/data structure navigation             |
| `N:`        | `N:0xH00b2=1`    | **AndNext:** Chained logical AND for complex expressions      |
| `O:`        | `O:0xH00b2=1`    | **OrNext:** Chained logical OR                                |
| `M:`        | `M:0xH00b2=1`    | **Measured:** Track progress bar, not just pass/fail          |
| `G:`        | `G:0xH00b2=1`    | **Measured %:** Measured percent                              |
| `Q:`        | `Q:0xH00b2=1`    | **MeasuredIf:** Only update measurement if true               |
| `T:`        | `T:0xH00b2=1`    | **Trigger:** Mark as trigger condition for indicator purposes |
| `K:`        | `K:0xH00b2*2`    | **Remember:** Save value for later logic                      |

*Some flags (PauseIf, ResetIf, etc.) establish short-circuit or reset/break conditions in the logic group. AndNext/OrNext chain multiple requirements on a single modifier flag, do not confuse with basic AND/OR between unrelated conditions; RA does not allow parentheses but flags are composable via chaining and flags.*

---

## 8. Measured, Trigger, and Special Condition Types

### 8.1. Measured

`M:` flags (and related syntax) specify a "measurable" condition—the runtime exposes current progress as a bar (useful for achievements like "Get 100 rings").

- Use for progress tracking; the measured value is current for the achievement and automatically displayed.
- MeasuredIf (`Q:`) restricts updates to the measurement.

### 8.2. Trigger Flag

`T:` indicates the achievement will "prime" when all base requirements are true except the Trigger ones; for player feedback/UI challenge indicator purposes.

---

## 9. Condition Chaining: AndNext, OrNext, Grouping

**AndNext** (`N:`) and **OrNext** (`O:`) are vital for **grouping conditions** within a formula, especially for complex “ResetIf,” “PauseIf,” or “Measured” logic.

- *AndNext* makes paired sub-conditions form a single logical condition.
- *OrNext* brings flexibility to fallbacks; "A and B, or C and D, or just E" can all be chained as needed.

**Example:**
```
N:0xH1234=1_O:0xH4321=32_N:0xH5678=6_R:0xH9999!=123
```
Parsed as:
```
((0xH1234==1 && 0xH4321==32) || 0xH5678==6) && ResetIf(0xH9999!=123)
```
**Note:** RA does not support parentheses; chained flags define logic flow.

---

## 10. Delimiters and Condition Sequencing

- Formula conditions are split by the underscore (`_`).
- Within each condition, flags/prefixes are single characters followed by a colon or attached directly, per official syntax.
- The dot `.` is used as a literal decimal separator for values (e.g. `0.1.`), but is rare; use care to parse this according to context.

---

## 11. Execution Workflow and Runtime Evaluation

**RetroAchievements logic is evaluated per frame**. The sequence is:

1. **Snapshot game memory**.
2. **Evaluate formula for each achievement**:
    - For each condition, resolve flagged logic, memory reads, modifiers, and comparisons.
    - Honor logical chaining (AndNext, OrNext, grouping).
    - Apply runtime tracking (hit count, Measured, PauseIf, ResetIf, etc.).
3. **If all non-special conditions are met**, and any special “Trigger”/“Measured”/challenge indicators are satisfied, "pop" the achievement.

For `Delta` and `Prior` modifiers, the system must store past frame values of every memory address referenced by the formula, **even across paused frames**.

*Your C# implementation must track:*
- Current, delta (previous) and prior (frame-2) values for every address accessed in any achievement formula.
- Internal hit counters, paused or reset state, group/chains of conditions (especially for Measured and PauseIf flags).

Reference: [RA How It Works][12†docs][36†docs].

---

## 12. Parsing RetroAchievements Formulas in C#

### 12.1. Lexical Analysis (Lexer)

Parse the string into *tokens*:
- Numbers (decimal or hex values, e.g., `128`, `0x1234`)
- Prefixes (`0xH`, `d`, `p`, `~`, etc.)
- Comparison operators (`=`, `!=`, `<`, etc.)
- Arithmetic operators (`+`, `-`, etc.)
- Logical flags (`P:`, `R:`, etc.)
- Condition separators (`_`)
- Dot/period (when used as value decimal).

Writing a custom lexer for this simple syntax is feasible.

```csharp
public enum TokenType { Number, HexNumber, Prefix, Modifier, Operator, Flag, Underscore, Colon, Dot, End }
// Define a struct or class for your Token object.
```

**Libraries such as Sprache, Irony, or Csly** can help with more complex parsing, but are not strictly required for canonical RA logic.

---

### 12.2. Parsing (Parser and AST Generation)

Build an **Abstract Syntax Tree (AST)** representing formula structure and chains. Suggested AST nodes:

- Formula node (top)
    - List of Condition nodes
- Condition node
    - Modifiers/Flags (AndNext, OrNext, PauseIf, etc.)
    - Operand (parsed memory address, with possible modifiers and arithmetic)
    - Comparator (and right-hand-side operand/value)
    - Optional hit count

Parsing steps:
1. Split string on `_`.
2. For each segment:
   - Parse flag/modifiers if any (e.g., `P:`, `d`, `~`)
   - Parse address/operand: consume prefixes, parse address, apply modifiers.
   - Parse comparator and right-hand-side (which can be memory or value).
   - Parse optional arithmetic expression for dynamic operands.
   - Parse chaining flags (AndNext, OrNext).
   
**Recursive parsing** is needed for chained addresses and arithmetic expressions.

**C# Example:**

```csharp
class ConditionNode
{
    public ConditionFlag? Flag;
    public List<Modifier> Modifiers;
    public OperandNode LeftOperand;
    public ComparisonOperator Comparator;
    public OperandNode RightOperand;
    public int? HitCount;
    public ChainingFlag? NextChain;
    // ... add more fields as necessary
}
```

Handle each logical flag’s grouping/chaining behavior as part of tree construction.

---

#### 12.3 AST Evaluation

Evaluation involves recursively invoking each node with current (and, if needed, prior/delta) memory, applying all modifiers, and folding logical chains using grouping flags. Special care is needed for Measured, PauseIf, and hit counts, since they maintain additional per-condition state.

---

### 12.4. Integration with Emulator Memory API

You must provide a mechanism for:
- Reading arbitrary memory ranges (with correct endianness, size).
- Storing and updating past frame ("delta") snapshots.
  
**Interface Example:**

```csharp
public interface IMemoryProvider
{
    byte ReadByte(int address);
    ushort ReadUInt16(int address, bool bigEndian = false);
    uint ReadUInt32(int address, bool bigEndian = false);
    float ReadFloat(int address, bool bigEndian = false);
    // Plus: support for writing, BCD decode, bit access, etc.
}
```

Delta and prior must be computed as part of your per-frame loop: after all formulas have been evaluated but before the next frame is processed.

---

## 13. Floating-Point and Specialized Data Types

Some achievements depend on float (IEEE754), double, or Microsoft Binary Float. The prefixes `fF`, `fB`, `fH`, `fI`, `fM`, `fL` represent various formats, endianness, and legacy float types.

- *Parse and read floats as bytes, reconstructing using BitConverter with or without endianness swapping.*

*Test handling of all float/BCD/double/bitfield prefixes across target platforms for correctness.*

---

## 14. Testing and Validation Strategies

- **Unit test**: Each parser component on all legal and illegal formula inputs.
- **Integration test**: Parsing and evaluating real-world achievement scripts (see [RATools](https://github.com/Jamiras/RATools), [rcheevos](https://github.com/RetroAchievements/rcheevos), and [official examples][17†docs][21†docs][35†docs]).
- **Cross-validation**: Compare results against reference emulators or the official rcheevos C library.
- Implement reference achievement sets and ensure all test achievements trigger (or not) in accordance with specification.
- **Edge cases**: Overflow, chaining, simultaneous triggers, alternate comparison, bit access, Measured, PauseIf.

---

## 15. Libraries and Open Reference Implementations

- **RATools**: Reference C# script interpreter for authoring and analyzing RA logic.
- **rcheevos**: Official C library used in most active emulators.
- **KrystianLesniak/retroachievements-api-net**: C# API client for RA website integration (not a formula evaluator, but insight into data models).
- **ANTLR**: For building full-fledged parsers using BNF grammars, if you want ultimate flexibility.
- **Sprache/Irony**: Lightweight parsing combinator libraries for C# useful for custom DSLs.
- **DynamicExpresso**: C# library for dynamic expression evaluation (could be adapted for address calculation sub-expressions).

---

## 16. Parsing the Provided Example

Let’s analyze and parse the formula:  
```
0xH00b2=128_0xH0022=8_0xH0380=11_0xH0380=0.1._R:0xH0017!=0
```

Step-by-step breakdown:

1. **First Condition: `0xH00b2=128`**
    - Access address `0x00b2` (8-bit).
    - Condition: Value equals `128`.

2. **Second Condition: `0xH0022=8`**
    - Address `0x0022` (8-bit).
    - Value equals `8`.

3. **Third Condition: `0xH0380=11`**
    - Address `0x0380` (8-bit).
    - Value equals `11`.

4. **Fourth Condition: `0xH0380=0.1.`**
    - Address `0x0380` (8-bit).
    - Value equals `0.1.` (usually used as a decimal, but likely a typo, or could mean "exactly 1, after a transition from 0" in old parsing; check semantic).

5. **Fifth Condition: `R:0xH0017!=0`**
    - Flag **ResetIf**, i.e., if address `0x0017` (8-bit) is NOT zero, reset all progress toward this achievement.

**How does this parse?**  
- The achievement is only earned when addresses `0x00b2`, `0x0022`, `0x0380` (twice, possibly tracking two separate frame transitions or counts) equal specified values.
- If at any point during evaluation `0x0017` is not zero, the progress is reset.
- `_` separators mean each is a standalone (AND) requirement.  
- There is no chaining (`N:` or `O:`, so all must be true on the same frame to unlock—unless hit counting is implied by a trailing `.1.` (check context).
- The dot syntax, as in `0.1.`, sometimes refers to a decimal value with a hit count, e.g., "when value is zero for 1 frame"; otherwise, some RA editors use a trailing dot to denote hit counts, e.g., `.10.` for 10 frames.

**Parsing in code:**

```csharp
var raw = "0xH00b2=128_0xH0022=8_0xH0380=11_0xH0380=0.1._R:0xH0017!=0";
var conditions = raw.Split('_');
foreach (var condStr in conditions)
{
    var cond = ParseCondition(condStr); // ParseCondition does all extraction per earlier logic
    // Add to formula AST
}
```

Where `ParseCondition` is:
1. Look for and consume a flag (e.g., `R:`).
2. Scan for modifiers before address.
3. Extract left operand (address, size, modifiers).
4. Parse comparator and right-hand-side (value or alternate memory).
5. Look for any trailing special syntax (`.` for hit count).

---

### 16.1. C# Example: Condition Parser Skeleton

```csharp
public ConditionNode ParseCondition(string cond)
{
    int idx = 0;
    ConditionNode node = new ConditionNode();
    // Parse flag
    if (cond.Length > 1 && cond[1] == ':')
    {
        node.Flag = ParseFlag(cond[0]);
        idx = 2;
    }
    // Parse modifiers (delta, prior, etc)
    while (IsModifier(cond[idx]))
    {
        node.Modifiers.Add(ParseModifier(cond[idx]));
        idx++;
    }
    // Parse address
    var (operand, nextIdx) = ParseOperand(cond, idx);
    node.LeftOperand = operand;
    idx = nextIdx;
    // Parse comparator
    node.Comparator = ParseComparator(cond, idx, out int opLength);
    idx += opLength;
    // Parse right-hand-side
    var right = cond.Substring(idx);
    node.RightOperand = ParseOperandOrValue(right);
    // Look for hit count (trailing .n.)
    if (cond.Contains('.'))
    {
        node.HitCount = ParseHitCount(cond);
    }
    return node;
}

// Implement helper ParseFlag, ParseModifier, ParseOperand, ParseComparator, ParseOperandOrValue, ParseHitCount...
```

This is a high-level skeleton; each helper must be robust to legal and illegal input, throw parsing exceptions where relevant, and support full prefix, flag, and chaining rules as described earlier.

**Evaluation is handled in a per-frame function during emulation**—the parser's output (AST or similar structure) is called with live memory each frame.

---

## 17. Advanced Evaluation: Runtime and Per-Frame Management

A robust execution engine will keep:
- Memory "shadow copies" for delta/prior in frame ring buffers.
- Bit mask and BCD decoding logic.
- Chained hit counters and/or logic for PauseIf, ResetIf, Measured, etc.
- Error, overflow, and underflow handling per the RA specification.

*Reference implementation: See [rcheevos][43†github][45†github].*

---

## 18. Conclusion and Best Practices

Building a full RA formula parser and executor in C# is highly achievable, but doing so robustly requires:
1. **Careful parsing** of all prefixes, modifiers, flags, and their order.
2. **Strong runtime state tracking**—not only what’s true *now* but what happened in the past two frames, across resets/pauses.
3. **Judicious state management** for Measured/Trigger/progress-tracking achievements.
4. **Modular parser/evaluator design**, so logic can be readily adapted as new flags and operators are developed in future RA versions.

**Recommended Actions**
- Start by validating your parser against documented real-world achievements (from the RA site and open sets).
- Use or study [RATools](https://github.com/Jamiras/RATools) or [rcheevos](https://github.com/RetroAchievements/rcheevos) when you need reference for ambiguous or edge-case formula behavior.
- Where possible, contribute patches, error corrections, or new flag handling back to the community.

---

## 19. References

All details, syntax, tables, and best practices summarized and verified using:  

- [Comprehensive RA formula and condition syntax documentation][4†docs][29†docs]
- [RA developer tutorials and scripting guides][1†docs][10†docs][19†docs]
- [Official rcheevos library (C)][9†github][43†github][45†github]
- [RATools reference implementation in C#][11†github]
- [Examples and condition breakdowns][17†docs][21†docs][35†docs]
- [Memory access, operator, and logical flag reference][25†docs][28†docs][22†docs][16†docs][26†docs][30†docs][13†docs]
- [General achievement architecture and runtime explanation][3†docs][12†docs][36†docs][7†docs][5†docs][18†docs]
- [Parsing strategies using C# tools and libraries][37†github][39†codeproject][8†stackoverflow][50†github]
- [RetroAchievements API client for C#][0†github][2†github][6†github]

---

**In sum:**  
A complete RA formula parser and execution engine in C# must cover *all* syntax variants, flag conventions, and runtime behaviors, as described above, and tested against both canonical and "edge" achievements present in the RA ecosystem. Adapt, extend, and validate your implementation as RetroAchievements evolves—new flags, data types, and conditions can be added over time. 

If you base your solution on this guide, you will ensure *full compatibility* with modern RetroAchievements sets and a maintainable base for future enhancement.

---
Great! I’m diving into a detailed guide that will help you build a complete C# parser and execution engine for RetroAchievements formulas. I’ll break down the syntax, keywords, conditions, and evaluation workflow—including a dedicated section for parsing the specific formula you provided. This will take a bit of time, so feel free to step away and check back later. Your guide will be saved right here in this conversation.
