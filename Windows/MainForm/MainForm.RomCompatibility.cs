using System;
using NesEmulator;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        // A CPU core and a ROM can be flatly incompatible: most of the named cores implement only
        // the 151 documented 6502 opcodes, but real cartridges - especially anything built with a
        // modern compiler like NESFab - lean on the undocumented ones (LAX/SAX/DCP/ISC/SLO/RLA/
        // SRE/RRA/...) as a matter of course. Only CPU_FIX, the accuracy target, implements the
        // full set; the others are frozen by design (see project_fix_core_family).
        //
        // Left alone, that combination fails in the worst possible way. The shipped default is
        // CrashBehavior.IgnoreErrors, under which an unimplemented opcode is silently executed as
        // a 2-cycle NOP - so the game does not stop, does not warn, and simply runs a different
        // program than the one on the cartridge. (With RedScreen it dies outright instead.) Either
        // way the player sees a broken game and has no way to know the CPU core is the reason.
        //
        // So before the ROM starts for real, run it briefly on a throwaway NES with invalid-opcode
        // reporting forced ON. This executes the ROM rather than guessing about it: a static scan
        // of PRG cannot tell code from data and would promote nearly every commercial ROM, which
        // would quietly make CPU_FIX the universal default and defeat the core-selection mechanic.
        // Probing has no false positives - it only fires when the CPU genuinely fetched an opcode
        // it cannot execute.
        //
        // The promotion is a RUNTIME override, not a config change: the player's chosen core stays
        // selected in the menu and comes back for the next ROM, exactly like the existing
        // per-session override used elsewhere in MainForm.Cores.
        private const int CoreCompatibilityProbeFrames = 90;

        // The rescue core. Always available regardless of progression (AlwaysAvailableCoreIds),
        // and the only CPU core implementing the undocumented opcodes.
        private const string CoreCompatibilityRescueCpu = "FIX";

        // Last auto-promotion, for the window title and for the HTTP status endpoint so automated
        // UAT can assert on it. Null when the selected cores ran the ROM as-is.
        private string? coreCompatibilityNotice;

        internal string? CoreCompatibilityNotice => coreCompatibilityNotice;

        private void EnsureCpuCoreCanRunRom(byte[] romData, string romName)
        {
            coreCompatibilityNotice = null;
            try
            {
                ProbeAndPromoteCpuCore(romData, romName);
            }
            finally
            {
                // Non-modal on purpose: a MessageBox here would block the headless UAT harness,
                // which drives ROM loads over the HTTP API with no one to dismiss a dialog.
                UpdateConsoleTitle();
                if (coreCompatibilityNotice != null) this.Text += $" - {coreCompatibilityNotice}";
            }
        }

        private void ProbeAndPromoteCpuCore(byte[] romData, string romName)
        {
            if (nes == null || romData == null || romData.Length == 0) return;

            var selected = string.IsNullOrWhiteSpace(runtimeCpuCoreOverride)
                ? config.SelectedCpuCore
                : runtimeCpuCoreOverride;
            if (string.IsNullOrWhiteSpace(selected)) return;

            // The rescue core implements everything; probing it against itself proves nothing.
            if (string.Equals(selected, CoreCompatibilityRescueCpu, StringComparison.OrdinalIgnoreCase)) return;

            if (ProbeCpuCore(romData, selected, out var failure)) return;

            // The selected core cannot run this ROM. Only promote if the rescue core actually can -
            // otherwise this is not a core-capability problem (a bad dump, an unsupported mapper)
            // and swapping cores would just relabel the failure.
            if (!ProbeCpuCore(romData, CoreCompatibilityRescueCpu, out var rescueFailure))
            {
                Console.WriteLine($"[CoreCompat] {romName}: CPU_{selected} failed ({failure}) and " +
                                  $"CPU_{CoreCompatibilityRescueCpu} failed too ({rescueFailure}) - " +
                                  "not a core-capability problem, leaving the selection alone.");
                return;
            }

            runtimeCpuCoreOverride = CoreCompatibilityRescueCpu;
            nes.SetCpuCore(CoreCompatibilityRescueCpu);

            coreCompatibilityNotice =
                $"CPU_{selected} cannot run this ROM ({failure}); using CPU_{CoreCompatibilityRescueCpu} instead";
            Console.WriteLine($"[CoreCompat] {romName}: {coreCompatibilityNotice}");
        }

        // Runs the ROM on a throwaway NES and reports whether the given CPU core survives.
        // CrashBehavior is forced to RedScreen for the probe REGARDLESS of the user's setting -
        // the whole point is to see the invalid opcode that IgnoreErrors would swallow. The
        // throwaway instance also keeps the probe clear of the real one's side effects (battery
        // RAM, mapper-30 flash, savestate slots).
        private static bool ProbeCpuCore(byte[] romData, string coreId, out string failure)
        {
            failure = string.Empty;
            try
            {
                var probe = new NES();
                probe.LoadROM(romData);
                probe.SetCpuCore(coreId);
                probe.SetCrashBehavior(NES.CrashBehavior.RedScreen);

                if (probe.IsCrashed())
                {
                    failure = probe.GetCrashInfo();
                    return false;
                }

                for (int f = 0; f < CoreCompatibilityProbeFrames; f++)
                {
                    probe.RunFrame();
                    if (probe.IsCrashed())
                    {
                        failure = probe.GetCrashInfo();
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // A throw out of the probe is itself a failure signal, but never a reason to stop
                // the player loading their ROM - the caller only ever uses this to pick a core.
                failure = ex.Message;
                return false;
            }
        }
    }
}
