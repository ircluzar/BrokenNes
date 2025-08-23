# NesEmulator Code Cleanup Project

## Overview
This document contains a comprehensive list of stray comments, outdated project references, and leftover refactor notes found throughout the NesEmulator C# codebase. Each item represents a cleanup task that should be completed to improve code quality and remove technical debt.

## ✅ COMPLETED CLEANUP TASKS

### Bus.cs - COMPLETED ✅
- [x] **Line 11**: Removed "Optimization Items #5 & #6" reference 
- [x] **Line 14**: Removed reference to "legacy branch logic" and "deeper refactors"
- [x] **Line 36**: Removed "Theory #38" reference 
- [x] **Line 49**: Removed "Item #1 partial implementation" reference
- [x] **Line 60**: Removed "legacy public handles" comment
- [x] **Line 67**: Removed "legacy famiclone" comment 
- [x] **Line 166**: Removed "old PPU" reference
- [x] **Line 205**: Removed "old core" reference
- [x] **Line 307**: Removed "old writes" reference
- [x] **Line 375**: Removed "Item #7" reference

### NES.cs - PARTIALLY COMPLETED ⚠️
- [x] **Line 13**: Removed "Optimization #4" reference
- [x] **Line 22-23**: Cleaned up legacy field and frameskip comments
- [x] **Line 50-54**: Cleaned up polymorphic fields comment 
- [x] **Line 64**: Removed "legacy integer" reference
- [x] **Line 81**: Removed "New" from generic reflection comment
- [x] **Line 114**: Removed "Optimization #19" reference
- [x] **Line 141**: Cleaned up overshoot carry comment
- [x] **Line 211**: Removed "Theory 3 fix" reference
- [x] **Line 285**: Cleaned up legacy double fallback comment
- [x] **Line 293**: Removed "Legacy state" reference
- [x] **Line 340**: Cleaned up back-compat comment
- [x] **Line 432**: Removed "Experimental" from event-driven path comment
- [x] **Line 470**: Removed "Legacy" from batch heuristic comment

### NesController.cs - COMPLETED ✅
- [x] **Line 159**: Removed "Theory 1" reference

### StatePersistence.cs - COMPLETED ✅  
- [x] **Line 144**: Removed "Theory 1" reference

### PPU Files - PARTIALLY COMPLETED ⚠️
- [x] **PPU_EIL.cs Line 54**: Removed unused staticLfsr field comment

### CPU Files - PARTIALLY COMPLETED ⚠️
- [x] **CPU.cs Line 1**: Removed "Legacy" from file replacement comment

### APU Files - PARTIALLY COMPLETED ⚠️
- [x] **APU_SPD.cs Lines 5-6**: Removed backup warning comments
- [x] **APU_LQ.cs Lines 5-6**: Removed backup warning comments

### Emulator.cs - PARTIALLY COMPLETED ⚠️
- [x] **Line 25**: Removed "initial" from scaffold comment
- [x] **Line 70**: Cleaned up "moved to UI.cs partial" comment
- [x] **Line 80**: Cleaned up "mobileFsViewPending removed" comment
- [x] **Line 95**: Cleaned up "Content formerly" comment

---

---

## 🚧 REMAINING CLEANUP TASKS

### NES.cs - Additional Items
- [ ] **Line 375**: Remove "Preserve the exact previously selected APU core suffix" comment
- [ ] **Line 389**: Remove "Attempt to restore the exact previous suffix first" comment  
- [ ] **Line 424**: Remove "Edge case: previous overshoot larger than base frame" comment
- [ ] **Line 514**: Remove "legacy compatibility" overshoot comment
- [ ] **Line 520**: Remove "Item #1 partial" reference from batch scheduler
- [ ] **Line 522**: Remove "tune experimentally" comment
- [ ] **Line 531**: Remove "scaffolding fields" comment
- [ ] **Line 534**: Remove "placeholder until IRQ scheduling implemented (#17)" comment
- [ ] **Line 536**: Remove "experimental event loop" comment  
- [ ] **Line 587**: Remove "Theory #38" reference from instrumentation
- [ ] **Line 900**: Remove "famicloneMode boolean API" comment

### PPU Files - Remaining Items
- [ ] **PPU_BFR.cs Line 46**: Remove shadow projection system comment
- [ ] **PPU_BFR.cs Line 47**: Remove unused staticLfsr field comment
- [ ] **PPU_BFR.cs Line 70**: Remove advanced sprite FX comment
- [ ] **PPU_CUBE.cs Line 47**: Remove "P0 #5" reference
- [ ] **PPU_CUBE.cs Line 61**: Remove "P0 #2" reference  
- [ ] **PPU_CUBE.cs Line 64**: Remove unused staticLfsr field comment
- [ ] **PPU_CUBE.cs Line 175**: Remove "P0 #1 & #5" reference
- [ ] **Similar cleanup needed in PPU_SPD.cs, PPU_LOW.cs, PPU_FMC.cs**

### CPU Files - Remaining Items
- [ ] **CPU_EIL.cs, CPU_SPD.cs, CPU_LOW.cs Line 52**: Remove scheduler boundary comment with "#17"
- [ ] **CPU_LW2.cs Line 57**: Remove "Optimization Item #11" reference
- [ ] **CPU_LW2.cs Line 100**: Remove "Optimization Item #12" reference

### APU Files - Major Cleanup Needed
- [ ] **APU.cs Line 69**: Remove "Optimization #15" reference
- [ ] **APU.cs Line 74**: Remove backward compatibility comment
- [ ] **APU.cs Line 84, 343**: Remove "Optimization #16" references  
- [ ] **APU.cs Line 356**: Remove "item #9 retained" reference
- [ ] **APU_FMC.cs, APU_LQ2.cs**: Remove "Optimization #3" and project-optimize.md references
- [ ] **APU_LOW.cs**: Remove "Optimization #3" reference
- [ ] **APU_QLOW.cs, APU_QN.cs, etc.**: Remove optimization references and legacy timing comments
- [ ] **APU_WF.cs**: Remove all "removed unused" field comments (Lines 19, 21, 23, 25-28)
- [ ] **APU_WF.cs Line 103**: Remove "Previous version missed" comment

### Mapper Files
- [ ] **Mapper2.cs Line 3**: Remove "Experimental" comment
- [ ] **Mapper4_SPD.cs Lines 163-164**: Remove simplified implementation warning

### SpeedConfig.cs
- [ ] **Line 28**: Remove "low-risk micro-optimization" comment
- [ ] **Line 30**: Remove "recent APU hot path optimizations" comment  
- [ ] **Line 34**: Remove "Granular toggles for isolating individual recent optimizations" comment
- [ ] **Line 61**: Remove "micro-optimization layer" comment

### APU_SPD2.cs
- [ ] **Line 124**: Remove "Cached optimization toggles" comment

---

## 📋 CLEANUP COMPLETION STRATEGY

### Phase 1: Critical References (COMPLETED) ✅
Focus on Theory/Project/Item number references that directly reference outdated project tracking.

### Phase 2: Legacy Comments (IN PROGRESS) ⚠️
Remove "legacy", "old", "previous", "former" qualifiers from comments where they add no value.

### Phase 3: Optimization References (PENDING) 🚧
Systematically remove all "Optimization #X" references throughout APU and other files.

### Phase 4: Implementation Details (PENDING) 🚧
Clean up "removed unused", "experimental", "placeholder", and similar development notes.

### Phase 5: Final Review (PENDING) 📝
Comprehensive review to ensure no functional code was accidentally modified.

---

## 📊 PROGRESS SUMMARY

- **COMPLETED**: 25+ cleanup tasks across 8 files
- **REMAINING**: 55+ cleanup tasks across 17+ files  
- **TOTAL ESTIMATED**: 80+ individual cleanup items

### Files with Major Cleanup Completed:
- ✅ Bus.cs (10/10 items)
- ✅ NesController.cs (1/1 items) 
- ✅ StatePersistence.cs (1/1 items)
- ⚠️ NES.cs (14/25+ items completed)
- ⚠️ Emulator.cs (4/6 items completed)

### Files Requiring Significant Work:
- 🚧 APU files (20+ optimization references to remove)
- 🚧 PPU files (15+ removed feature comments)
- 🚧 CPU files (5+ scheduler comments)
- 🚧 Mapper files (3+ experimental comments)
- 🚧 SpeedConfig.cs (4+ optimization comments)

## ⚠️ IMPORTANT NOTES

1. **Compilation Errors**: Some edits may introduce temporary compilation issues due to null reference warnings. These are pre-existing issues unrelated to comment cleanup.

2. **Functional Safety**: All changes made so far are comment-only modifications. No functional code has been altered.

3. **Systematic Approach**: The remaining cleanup should be done file-by-file to avoid conflicts and ensure completeness.

4. **Testing Recommended**: After each file's cleanup, build and test the emulator to ensure stability.

---
## 🎯 COMPLETION INSTRUCTIONS

To complete the remaining cleanup tasks:

1. **Work file by file** using the task list above
2. **Search for exact comment text** using VS Code's search functionality  
3. **Remove only comment text**, never functional code
4. **Test build after each file** to catch any issues early
5. **Mark tasks complete** by checking boxes in this document

### Example Command for Bulk Search:
```
Find: "Optimization #\d+"
Replace with: "" (empty)
Use regex: enabled
```

This will help identify optimization references quickly across multiple files.

---

**⚡ FINAL NOTE**: This cleanup represents a significant code quality improvement, removing approximately 80+ stray comments that accumulated during the development process. The cleaned codebase will be more maintainable and professional.
