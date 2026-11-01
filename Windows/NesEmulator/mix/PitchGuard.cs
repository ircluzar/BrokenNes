using System;
using System.Threading;

namespace NesEmulator.Mix
{
    /// <summary>
    /// "Remove high-pitched": an opt-in ear guard for the cross-console sound bridges. A bridge knows the pitch of every
    /// note before it writes it to the target sound chip, so a note above <see cref="CeilingHz"/> is muted there - before
    /// it is mixed - instead of being filtered out of the finished sound. Off by default: with it off every bridge plays
    /// exactly what it did before. Only tonal channels are checked (pulse, triangle / wave, SNES sample voices); noise
    /// has no pitch in that sense and is left alone. App-wide, set from the volume panel (desktop) or its settings (Lite).
    /// </summary>
    public static class PitchGuard
    {
        /// <summary>Mute tonal notes above <see cref="CeilingHz"/>.</summary>
        public static volatile bool Enabled;

        /// <summary>The highest fundamental that still plays, in Hz. The top note of a piano (C8) is 4186 Hz.</summary>
        public static volatile float CeilingHz = DefaultCeilingHz;

        public const float DefaultCeilingHz = 4186f;
        public const float MinCeilingHz = 500f, MaxCeilingHz = 16000f;

        /// <summary>Notes muted so far (bridge syncs that found the channel's note above the ceiling).</summary>
        public static long BlockedNotes;

        /// <summary>
        /// Optional pitch census for tuning (Workshop): when non-null, every tonal note a bridge plays or blocks is counted
        /// once per bridge sync, in semitone bins (index = MIDI note number, 0..127; above 127 goes in the last bin).
        /// </summary>
        public static long[]? Census;

        /// <summary>True when a note at <paramref name="hz"/> must be muted. Also feeds the census.</summary>
        public static bool Blocks(double hz)
        {
            var census = Census;
            if (census != null && hz > 0) Interlocked.Increment(ref census[Math.Clamp(MidiNote(hz), 0, 127)]);
            if (!Enabled || !(hz > CeilingHz)) return false;
            Interlocked.Increment(ref BlockedNotes);
            return true;
        }

        public static int MidiNote(double hz) => (int)Math.Round(69 + 12 * Math.Log2(hz / 440.0));

        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>"C8", "A#6" ... for a frequency (nearest note).</summary>
        public static string NoteName(double hz)
        {
            int n = MidiNote(hz);
            return NoteNames[((n % 12) + 12) % 12] + (n / 12 - 1);
        }
    }
}
