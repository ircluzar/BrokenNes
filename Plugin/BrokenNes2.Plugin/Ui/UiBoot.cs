// Where the real models register themselves with the tabs (UiServices), without anyone editing a shared file.
//
// The editor calls UiBoot.Run() the first time a window is created. It calls two partial methods, one per model, each meant to be IMPLEMENTED IN THE OWNER'S OWN FILE:
//
//     // Plugin/BrokenNes2.Plugin/Instruments/InstrumentsUi.cs            (the `imodel` agent)
//     namespace BrokenNes2.Ui;
//     internal static partial class UiBoot
//     {
//         static partial void RegisterInstrumentModel() => UiServices.Instruments = emulator => new MyInstrumentProvider(emulator);
//     }
//
//     // ... and the `presets` agent, in its own file:
//         static partial void RegisterPresetLibrary() => UiServices.Presets = new MyPresetLibrary();
//
// A partial method with no implementation costs nothing and the tab then says "not connected in this build". (A [ModuleInitializer] would do the same job,
// but it has not been tried under Native AOT here; this seam has the same effect and is certain.)
namespace BrokenNes2.Ui;

internal static partial class UiBoot
{
    private static bool ran;

    public static void Run()
    {
        if (ran) return;
        ran = true;
        RegisterInstrumentModel();
        RegisterPresetLibrary();
    }

    static partial void RegisterInstrumentModel();
    static partial void RegisterPresetLibrary();
}
