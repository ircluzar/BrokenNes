// Round 6 (inject): the handler the Instruments tab's "Push into the game" calls (InstrumentPush.Handler). It asks the emulator to push the edited instrument into the running game (Emulator.PushInstrument, which holds the emulator's lock and
// tells the corruption farm) and turns the library's answer into the words the tab shows. Excluded from the FruityHost build like InstrumentsUi.cs (it needs the Emulator).
using BrokenNes2.Ui;

namespace BrokenNes2.Instruments;

internal static class EmulatorPush
{
    public static ActionResult Handle(Emulator emu, Instrument edited, Instrument? clean)
    {
        var o = emu.PushInstrument(edited, clean);
        return o.Ok ? ActionResult.Done(o.Message) : ActionResult.Refused(o.Message);
    }
}
