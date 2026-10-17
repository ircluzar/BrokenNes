// Round 6 (inject): test hooks 320-339 of the plugin, for the end-to-end checks of "push into the game" (the FruityHost `inject` check drives them).
using BrokenNes2.Instruments;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private static string lastInjectResult = "";
    private static Instrument? injectClean;

    /// <summary>Test hooks: 320 push instrument <c>index</c> of the frozen working copy into the running game (the clean instrument set by 321, if any; returns 0 done, 1 refused, 2 no such instrument), 321 set the instrument as the game had it (packed, <c>index</c> bytes at <c>value</c>; 0 bytes forgets it),
    /// 322 take instrument <c>index</c> out of the game (0 done, 1 refused), 323 how many instruments are pushed (-1 no emulator), 324 copy the last push or removal's words into the buffer at <c>value</c> (at most <c>index</c> chars; returns the length),
    /// 325 copy the inject note into the buffer (returns its length, -1 none), 326 hash of the game's own audio RAM (SNES; 0 none), 327 the project section's size (0 none), 328 whether the hooks are in the sound core (1 yes, 0 lost, -1 nothing pushed),
    /// 329 take everything out of the game, 330 switch one of the library's deliberate bugs on (<c>value</c>, 0 = none), 331 hash of the game's own YM2612 patch registers (Genesis; 0 none), 332 poke a byte of the game's audio RAM (<c>index</c> address, <c>value</c> byte; 1 done), 333 how many memory writes the corruption farm holds (-1 none).</summary>
    private nint InjectTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 320:
            {
                var inst = emu?.InstrumentSource?.Read((int)index);
                if (emu == null || inst == null) { lastInjectResult = "no such instrument"; return 2; }
                var o = emu.PushInstrument(inst, injectClean);
                lastInjectResult = o.Message;
                return o.Ok ? 0 : 1;
            }
            case 321:
            {
                injectClean = index <= 0 ? null : InstrumentCodec.Unpack(new ReadOnlySpan<byte>((void*)value, (int)index).ToArray(), out _);
                return 0;
            }
            case 322:
            {
                var inst = emu?.InstrumentSource?.Read((int)index);
                if (emu == null || inst == null) { lastInjectResult = "no such instrument"; return 1; }
                var o = emu.RemovePush(inst);
                lastInjectResult = o.Message;
                return o.Ok ? 0 : 1;
            }
            case 323: return emu == null ? -1 : emu.PushedCount;
            case 324: return CopyText(lastInjectResult, index, value);
            case 325: return emu?.InjectNote is { } note ? CopyText(note, index, value) : -1;
            case 326: return (nint)(emu?.LiveSoundHashForTests() ?? 0);
            case 327: return emu?.CaptureInjectSection()?.Length ?? 0;
            case 328: return emu == null || emu.PushedCount == 0 ? -1 : emu.VerifyPushes() == null ? 1 : 0;
            case 329:
            {
                var o = emu?.RemoveAllPushes() ?? PushOutcome.Refused("no emulator");
                lastInjectResult = o.Message;
                return o.Ok ? 0 : 1;
            }
            case 330: PushMutants.Active = (int)value; return 0;
            case 331: return (nint)(emu?.LiveSoundHashForTests() ?? 0);
            case 332: return emu?.PokeDomainForTests("ARAM", (int)index, (byte)value) == true ? 1 : 0;
            case 333: return emu?.FarmWritesForTests() ?? -1;
            case 334: return emu?.PokeDomainForTests("DSP", (int)index, (byte)value) == true ? 1 : 0;
            default: return 0;
        }
    }
}
