# BrokenNes Future Features & Improvements

This document serves as a work reference for planned features and improvements that emerged from the major refactor. Items are organized by priority and implementation timeline.

---

## 🚀 **HIGH PRIORITY TASKS**

### **Static Analysis & Code Cleanup**
**Effort**: Low-Medium | **Priority**: HIGH - Should be done soon after refactor completion

**What it does**: Runs comprehensive static analysis to identify and remove unused methods, dead code, and duplicate logic throughout the codebase. Also includes cleanup of old leftover comments (theories, implementation comments) and other remnants that should have been removed.

**Purpose**: Reduces codebase size and complexity, improves compilation times and IDE performance, eliminates maintenance burden of unused code, provides cleaner codebase for new developers.

#### **Tasks:**
- [ ] **Run Static Analysis Tools**
  - [ ] Use Visual Studio Code Analysis to identify unused code
  - [ ] Run ReSharper/Rider inspections if available
  - [ ] Use `dotnet analyze` command for additional insights
  - [ ] Generate report of findings

- [ ] **Remove Dead Code & Unused Methods**
  - [ ] Remove unused private methods identified by analysis
  - [ ] Remove unused fields and properties
  - [ ] Remove unused using statements
  - [ ] Remove unreachable code blocks

- [ ] **Clean Up Comments & Documentation**
  - [ ] Remove old implementation theories and temporary notes
  - [ ] Remove outdated TODO comments that are no longer relevant
  - [ ] Remove debug/development comments that shouldn't be in production
  - [ ] Update XML documentation comments to match current implementation
  - [ ] Remove commented-out code blocks

- [ ] **Verify & Test Changes**
  - [ ] Build project to ensure no breaking changes
  - [ ] Run basic functionality tests
  - [ ] Review changes with git diff before committing
  - [ ] Document what was removed in commit message

---

## 🎨 **MEDIUM PRIORITY TASKS**

### **Component Extraction**
**Effort**: Medium-High | **Priority**: MEDIUM - Good candidate for next major UI improvement

**What it does**: Creates dedicated Blazor components for major UI panels: ROM Manager, Corruptor, Glitch Harvester, and Benchmark modals.

**Purpose**: Drastically reduces the size and complexity of `Nes.razor`, improves component reusability and maintainability, enables focused testing of individual UI sections, creates cleaner separation of concerns in the view layer.

#### **Tasks:**
- [ ] **Planning & Preparation**
  - [ ] Analyze current `Nes.razor` to identify component boundaries
  - [ ] Design component parameter contracts (what props each component needs)
  - [ ] Plan event handling strategy (callbacks vs. direct emulator access)
  - [ ] Create component folder structure

- [ ] **ROM Manager Component**
  - [ ] Create `RomManagerPanel.razor` component
  - [ ] Extract ROM table HTML and styling from `Nes.razor`
  - [ ] Set up component parameters (Emulator, FileInput reference, etc.)
  - [ ] Implement event handlers for ROM operations
  - [ ] Test ROM upload, delete, and selection functionality
  - [ ] Update `Nes.razor` to use the new component

- [ ] **Corruptor Component**
  - [ ] Create `CorruptorPanel.razor` component
  - [ ] Extract Real-Time Corruptor HTML and styling
  - [ ] Set up component parameters and event bindings
  - [ ] Test corruption functionality (blast, auto-corrupt, settings)
  - [ ] Update `Nes.razor` to use the new component

- [ ] **Glitch Harvester Component**
  - [ ] Create `GlitchHarvesterPanel.razor` component
  - [ ] Extract GH HTML and styling from `Nes.razor`
  - [ ] Implement base state management UI
  - [ ] Implement stash and stockpile UI sections
  - [ ] Test all GH operations (add base, corrupt & stash, replay, etc.)
  - [ ] Update `Nes.razor` to use the new component

- [ ] **Benchmark Modal Components**
  - [ ] Create `BenchmarkModal.razor` component
  - [ ] Create `BenchmarkComparisonModal.razor` component
  - [ ] Extract benchmark modal HTML and complex chart rendering
  - [ ] Implement modal state management
  - [ ] Test benchmark execution and history display
  - [ ] Update `Nes.razor` to use the new components

- [ ] **Final Integration & Testing**
  - [ ] Verify all extracted components work correctly
  - [ ] Ensure styling is preserved
  - [ ] Test responsiveness and mobile views
  - [ ] Clean up `Nes.razor` and remove extracted markup
  - [ ] Document new component structure

---

### **UI Update Throttling**
**Effort**: Medium | **Priority**: MEDIUM - Performance improvement with good ROI

**What it does**: Adds cancellation and rate-limiting for high-frequency UI updates like FPS counters and real-time statistics.

**Purpose**: Prevents UI thread blocking during intensive operations, improves overall application responsiveness, reduces unnecessary re-renders and computation, provides smoother user experience.

#### **Tasks:**
- [ ] **Analysis & Profiling**
  - [ ] Identify all high-frequency update paths in the application
  - [ ] Profile current UI performance during intensive operations
  - [ ] Measure current update frequencies (FPS display, benchmark progress, etc.)
  - [ ] Document performance bottlenecks and update patterns

- [ ] **Implement FPS Display Throttling**
  - [ ] Add throttling to FPS counter updates (limit to ~10 updates/second)
  - [ ] Implement debouncing for FPS display changes
  - [ ] Test FPS display performance during emulation

- [ ] **Implement Statistics Update Throttling**
  - [ ] Throttle real-time corruption statistics updates
  - [ ] Add rate limiting to benchmark progress updates
  - [ ] Implement throttling for memory usage displays
  - [ ] Add debouncing for other real-time metrics

- [ ] **Add Cancellation Support**
  - [ ] Implement CancellationToken support for long-running benchmark operations
  - [ ] Add cancellation for corruption operations that can be interrupted
  - [ ] Ensure proper cleanup when operations are cancelled
  - [ ] Test cancellation behavior in UI

- [ ] **Observable Patterns (Optional)**
  - [ ] Evaluate implementing Observable patterns for rate-limited updates
  - [ ] Consider using reactive extensions for complex update scenarios
  - [ ] Implement if beneficial for maintainability

- [ ] **Testing & Validation**
  - [ ] Measure performance improvements after implementation
  - [ ] Test UI responsiveness during intensive operations
  - [ ] Verify smooth user experience across different scenarios
  - [ ] Document performance gains achieved

---

## � **FUTURE CONSIDERATIONS - KEEP FOR POSSIBLE PLANNING**

### **Lazy Loading & Virtualization**
**What it does**: Implements virtualization for large lists like benchmark history and glitch harvester stockpile to improve performance with large datasets.

**Purpose**:
- Improves UI responsiveness when dealing with hundreds/thousands of items
- Reduces memory footprint by only rendering visible items
- Provides better user experience during heavy usage

**Implementation Notes**:
- First, profile current performance to identify actual bottlenecks
- Consider implementing when user datasets grow beyond comfortable limits
- Evaluate Blazor virtualization components or custom solutions
- Focus on most problematic lists (likely benchmark history)

**Effort**: Medium - Requires performance profiling to identify bottlenecks first
**Priority**: FUTURE - Implement when performance issues are identified

---

### **Dependency Injection Registration**
**What it does**: Registers `Emulator` as a scoped service in the DI container and converts `Nes.razor` to inject it instead of creating per-page instances.

**Purpose**:
- Enables cross-route persistence of emulator state
- Provides better integration with ASP.NET Core patterns
- Improves testability through constructor injection
- Allows for sophisticated lifetime management

**Implementation Notes**:
- Evaluate if cross-route persistence is actually needed
- Consider implications of shared state between browser tabs
- May require state management architecture changes
- Could improve or complicate current simple lifecycle

**Effort**: Medium - Requires lifecycle analysis and potential state management changes
**Priority**: FUTURE - Consider when multi-route navigation is implemented

---

## 📋 **Implementation Roadmap**

### **Phase 1: Immediate Cleanup (High Priority)**
#### **Static Analysis & Code Cleanup**
- [ ] Complete all static analysis tasks
- [ ] Remove dead code and unused methods  
- [ ] Clean up old comments and implementation notes
- [ ] Update documentation to reflect new architecture

### **Phase 2: UI Improvements (Medium Priority)**
#### **UI Update Throttling**
- [ ] Profile current update patterns
- [ ] Implement throttling for FPS and statistics updates
- [ ] Add cancellation support for long-running operations
- [ ] Measure and document performance improvements

#### **Component Extraction**  
- [ ] Extract ROM Manager panel component
- [ ] Extract Corruptor panel component
- [ ] Extract Glitch Harvester panel component
- [ ] Extract Benchmark modal components
- [ ] Update `Nes.razor` to use all new components

### **Phase 3: Future Enhancements (As Needed)**
- [ ] **Lazy Loading** - Only if performance issues are identified
- [ ] **Dependency Injection** - Only if multi-route functionality is needed

---

## 🎯 **Success Metrics**

### **Code Quality Metrics**
- Reduction in codebase size after dead code cleanup
- Improvement in build times
- Reduced complexity metrics (cyclomatic complexity, maintainability index)

### **Performance Metrics**
- UI responsiveness during intensive operations
- Memory usage with large datasets
- Frame rate stability during emulation

### **Maintainability Metrics**
- Time to onboard new developers
- Time to implement new features
- Number of bugs introduced during changes

---

*Last Updated: August 2025*
*Status: Work Document - Actively Maintained*
