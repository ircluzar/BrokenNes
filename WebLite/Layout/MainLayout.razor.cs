using Microsoft.AspNetCore.Components;

namespace BrokenNes.Layout;

public partial class MainLayout : LayoutComponentBase
{
    // Explicit parameterless constructor ensures it's preserved under aggressive trimming/AOT.
    public MainLayout() { }
}
