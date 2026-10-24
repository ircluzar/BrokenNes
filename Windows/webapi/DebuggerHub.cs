using System;
using NesEmulator;
using NesEmulator.Debugging;
using NesEmulator.Systems;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// The desktop app's debugger: one per running game, made on the first control request (so a game nobody debugs pays nothing), and
    /// released (every hook removed, a parked emulation let go) whenever the emulation stops or the game changes.
    /// </summary>
    public sealed class DebuggerHub
    {
        private readonly object sync = new();
        private object? target;
        private Bus? targetBus;
        private DebugMachine? machine;
        private Debugger? debugger;
        private string? machineError;

        /// <summary>The emulation lock: a parked emulation thread releases it, so the app and the HTTP API stay responsive.</summary>
        public object? HostLock { get; set; }

        /// <summary>The debugger's view of the running game (no hooks: reading registers and memory costs the game nothing), or null.</summary>
        public DebugMachine? Machine(object? game, out string? why)
        {
            lock (sync) { Retarget(game); why = machineError; return machine; }
        }

        /// <summary>The attached debugger, or null.</summary>
        public Debugger? Current(object? game)
        {
            lock (sync) { Retarget(game); return debugger; }
        }

        /// <summary>Attaches (or returns) the debugger of the running game. It tracks the call stack from now on.</summary>
        public Debugger Attach(object? game)
        {
            lock (sync)
            {
                Retarget(game);
                if (machine == null) throw new InvalidOperationException(machineError ?? "no game is running");
                if (debugger == null)
                {
                    debugger = new Debugger(machine) { HostLock = HostLock };
                    debugger.TrackCallStack = true;
                }
                return debugger;
            }
        }

        /// <summary>Detaches the debugger (the machine runs as if it never had one) and forgets the game.</summary>
        public void Release()
        {
            Debugger? d;
            lock (sync) { d = debugger; debugger = null; machine = null; target = null; targetBus = null; machineError = null; }
            d?.Detach();
        }

        /// <summary>Detaches the debugger but keeps the game (POST /api/debug/detach).</summary>
        public bool Detach()
        {
            Debugger? d;
            lock (sync) { d = debugger; debugger = null; }
            d?.Detach();
            return d != null;
        }

        private void Retarget(object? game)
        {
            var bus = (game as NES)?.DebugBus;
            if (ReferenceEquals(game, target) && ReferenceEquals(bus, targetBus)) return;
            debugger?.Detach();
            debugger = null;
            target = game; targetBus = bus; machine = null; machineError = null;
            try
            {
                machine = game switch
                {
                    NES n when n.DebugBus != null => DebugMachines.For(n),
                    IConsoleSession s => DebugMachines.For(s),
                    null => null,
                    _ => null,
                };
                if (machine == null) machineError = game == null ? "no game is running" : "no ROM is loaded";
            }
            catch (Exception e) { machineError = e.Message; }
        }
    }
}
