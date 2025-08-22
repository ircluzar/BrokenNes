# Final Extraction Plan

## Overview
After completing the major extraction work in `extract-plan.md`, we still have significant code-behind logic in `Pages/Nes.razor` that can be further extracted to achieve a cleaner separation of concerns. This plan focuses on the remaining code that can reasonably be moved elsewhere while maintaining the Blazor component's essential UI functionality.

## Current State Analysis

### What Remains in Pages/Nes.razor (@code block):
1. **Component Lifecycle Methods**: `OnInitialized()`, `OnAfterRenderAsync()`, `Dispose()`
2. **Property Delegates**: Simple property forwarding to `emu.Controller` and `emu.Corruptor`
3. **UI Helper Properties**: `FilteredRomOptions`, `IsBuiltInSelected`, `romOptions`, `uploadedRoms`
4. **Local UI Event Handlers**: Form change handlers, DOM interaction methods
5. **ElementReference Fields**: `fileInput` for file uploads
6. **Small UI Convenience Methods**: Input validation, simple delegations

### What Cannot Be Extracted:
- Blazor component lifecycle hooks (`OnInitialized`, `OnAfterRenderAsync`)
- ElementReference fields (must be in component for @ref binding)
- Component-specific UI state and rendering context
- Direct @bind event handlers that require component context

## Extraction Targets

### Phase 1: Create UI Service Layer
- [ ] Create `NesEmulator/UI/NesPageService.cs` to handle:
  - ROM filtering and search logic
  - Form validation helpers  
  - UI state computation (IsBuiltInSelected, etc.)
  - File input processing coordination

### Phase 2: Extract Event Handlers
- [ ] Move form change handlers to UI service:
  - `OnCrashBehaviorChanged()` → UI service method
  - `OnIntensityChange()` / `OnIntensityBoxChange()` → Centralized in Emulator
  - `DomainsChanged()` → Proper multiple select handling in UI service
  - `OnRawFileSelected()` → File handling coordination

### Phase 3: Extract Computed Properties
- [ ] Move computed properties to UI service:
  - `FilteredRomOptions` → UI service with observable/computed pattern
  - ROM-related helper properties
  - UI state calculations

### Phase 4: Minimize Component Lifecycle
- [ ] Reduce `OnInitialized()` to bare minimum
- [ ] Streamline `OnAfterRenderAsync()` to essential DOM setup only
- [ ] Simplify `Dispose()` to basic cleanup delegation

### Phase 5: Create UI Helpers Static Class
- [ ] Extract remaining utility methods to static helpers:
  - Input parsing/validation
  - String formatting helpers
  - Simple computational methods

## Implementation Strategy

### Step 1: Create NesPageService
```csharp
// NesEmulator/UI/NesPageService.cs
public class NesPageService
{
    private readonly Emulator _emulator;
    
    public NesPageService(Emulator emulator)
    {
        _emulator = emulator;
    }
    
    // ROM filtering logic
    public IEnumerable<RomOption> GetFilteredRoms(string searchText)
    
    // Form event handling
    public void HandleCrashBehaviorChanged(string newBehavior)
    public void HandleIntensityChange(int newIntensity)
    
    // UI state computation
    public bool IsBuiltInRomSelected()
    public RomStats ComputeRomStats()
}
```

### Step 2: Update Emulator to Include UI Service
```csharp
// In Emulator.cs constructor
public IUiService UiService { get; private set; }

private void Initialize()
{
    // ... existing initialization
    UiService = new NesPageService(this);
}
```

### Step 3: Update Component to Use Service
```csharp
// In Nes.razor @code
// Replace direct logic with service calls
private IEnumerable<RomOption> FilteredRomOptions => emu!.UiService.GetFilteredRoms(emu.Controller.RomSearch);
private bool IsBuiltInSelected => emu!.UiService.IsBuiltInRomSelected();

private void OnCrashBehaviorChanged(ChangeEventArgs e) => 
    emu!.UiService.HandleCrashBehaviorChanged(e.Value?.ToString() ?? "");
```

## Benefits

### Achieved Separation:
- **UI Logic**: Extracted to dedicated service layer
- **Component**: Focused purely on rendering and DOM interaction  
- **Business Logic**: Already centralized in Emulator
- **Event Handling**: Proper delegation chain

### Maintainability Improvements:
- **Testability**: UI service can be unit tested independently
- **Reusability**: UI logic could be shared with other components
- **Single Responsibility**: Each class has a clear, focused purpose
- **Dependency Management**: Clear service injection patterns

## Validation Criteria

### Build Success:
- [ ] No compilation errors
- [ ] All existing functionality preserved
- [ ] Performance maintained

### Functionality Tests:
- [ ] ROM filtering works correctly
- [ ] All form controls function properly
- [ ] File uploads still work
- [ ] Component lifecycle operates normally
- [ ] Event handling maintains responsiveness

### Architecture Quality:
- [ ] Clean dependency injection
- [ ] Proper abstraction layers
- [ ] Minimal component footprint
- [ ] Clear service boundaries

## Future Considerations

### Potential Next Steps:
- Create `IUiService` interface for better testability
- Extract more complex UI patterns to reusable components
- Consider moving large nested components (benchmarks modal, comparison modal) to separate components
- Implement proper state management patterns if complexity grows

### Limitations:
- Some Blazor-specific code must remain in the component
- ElementReference usage cannot be fully extracted
- Component lifecycle hooks are inherently component-bound
- @bind directives require component context

## Expected Outcome

After completion, `Pages/Nes.razor` should be reduced to:
1. **Minimal @code block** (< 50 lines) with only essential component concerns
2. **Clean service delegation** for all business logic
3. **Focused responsibility** on UI rendering and DOM interaction only
4. **Maintainable architecture** with proper separation of concerns

This represents the practical limit of extraction while maintaining Blazor component functionality and performance.
