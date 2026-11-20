using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using cor64.Mips;
using cor64.Mips.R4300I;
using NLog;

namespace cor64
{
    public class SingleThreadHost
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private readonly N64System m_System;
        private bool m_Running;
        private Exception m_Exception;
        private readonly AutoResetEvent m_StartWaitEvent;
        public event Action Break;
        private Timer m_SiReadyDelayTimer;

        private Thread m_RspThread;

        private bool m_SignalExit = false;
        // [cer64plus] COR64_BATCH=<n> instructions per slice (default 16; 1 = original per-instruction interleave)
        private static readonly int s_Batch = int.TryParse(Environment.GetEnvironmentVariable("COR64_BATCH"), out var b) && b >= 1 ? b : 16;
        private readonly ManualResetEvent m_ExitWait = new ManualResetEvent(true);

        public static SingleThreadHost Current { get; private set; }

        public SingleThreadHost(N64System system)
        {
            Current = this;
            m_System = system;
            m_StartWaitEvent = new AutoResetEvent(false);
        }

        public void SignalExit() {
            m_SignalExit = true;
            m_ExitWait.WaitOne();
        }

        // [cer64plus] savestate requests, executed between batches on the emulation thread
        private volatile string m_SaveRequest, m_LoadRequest;
        public void RequestSave(string path) => m_SaveRequest = path;
        public void RequestLoad(string path) => m_LoadRequest = path;
        public static readonly string AutoSave = Environment.GetEnvironmentVariable("COR64_SAVE_AT_VI");   // "<vi>:<path>"
        private bool m_AutoSaveDone;

        // [cer64plus] COR64_BENCH_VI=<n>: after the first batch (and any COR64_LOAD), run n VI interrupts, print the wall time, exit.
        private static readonly long s_BenchVi = long.TryParse(Environment.GetEnvironmentVariable("COR64_BENCH_VI"), out var bv) ? bv : 0;
        private long m_BenchStartVi = -1;
        private System.Diagnostics.Stopwatch m_BenchWatch;

        private void BenchCheck()
        {
            if (m_LoadRequest != null) return;
            if (m_BenchStartVi < 0) { m_BenchStartVi = Telemetry.ViInterrupts; m_BenchWatch = System.Diagnostics.Stopwatch.StartNew(); return; }
            if (Telemetry.ViInterrupts - m_BenchStartVi >= s_BenchVi) {
                Console.WriteLine("BENCH vi={0} ms={1} ticks={2} idleSkipped={3}", s_BenchVi, m_BenchWatch.ElapsedMilliseconds, Telemetry.Ticks, cor64.Mips.R4300I.Interpreter.IdleSkippedSteps);
                Console.WriteLine("RSPJIT blocks={0} runs={1} fallback={2} ilCompiled={3}", cor64.Mips.Rsp.RspInterpreter.JitBlocksCompiled, cor64.Mips.Rsp.RspInterpreter.JitBlockRuns, cor64.Mips.Rsp.RspInterpreter.JitFallbackSteps, cor64.Mips.Rsp.RspInterpreter.JitILCompiled);
                if (cor64.Mips.R4300I.Interpreter.s_OpStats) {
                    var c = cor64.Mips.R4300I.Interpreter.OpCounts; long tot = 0; foreach (var x in c) tot += x;
                    var idx = Enumerable.Range(0, c.Length).Where(i => c[i] > 0).OrderByDescending(i => c[i]).Take(45);
                    foreach (var i in idx) Console.WriteLine("OPSTAT {0,-10} {1,12} {2,6:F2}%", cor64.Mips.R4300I.Interpreter.OpNames[i], c[i], 100.0 * c[i] / tot);
                }
                Console.Out.Flush();
                Environment.Exit(0);
            }
        }

        private void ServiceStateRequests()
        {
            if (AutoSave != null && !m_AutoSaveDone) {
                var parts = AutoSave.Split(':', 2);
                if (Telemetry.ViInterrupts >= long.Parse(parts[0])) { m_AutoSaveDone = true; m_SaveRequest = parts[1]; }
            }
            var s = m_SaveRequest;
            if (s != null) { m_SaveRequest = null; var sw = System.Diagnostics.Stopwatch.StartNew(); SaveState.Save(m_System, s); Log.Info("Savestate saved: {0} at VI {1} audioFrame {3} ({2} ms)", s, Telemetry.ViInterrupts, sw.ElapsedMilliseconds, (m_System.Apu as AiDac)?.FramesPushed); }
            var l = m_LoadRequest;
            if (l != null) { m_LoadRequest = null; var sw = System.Diagnostics.Stopwatch.StartNew(); SaveState.Load(m_System, l); Log.Info("Savestate loaded: {0}, now VI {1} ({2} ms)", l, Telemetry.ViInterrupts, sw.ElapsedMilliseconds); }
        }

        private void RunLoop()
        {
            m_Exception = null;
            m_Running = true;
            m_StartWaitEvent.Set();

            bool paused = true;

            var debugger = m_System.Dbg;

            // m_SiReadyDelayTimer = new Timer((_) => m_System.DeviceRcp.SerialDevice.SignalSiReady(), null, 7000, 0);

            debugger.DebugBreak += () => {
                m_System.DeviceCPU.Debugger.Break();
                m_System.DeviceRcp.DeviceRsp.Debugger.Break();
                Log.Info("BREAK...@" + m_System.DeviceCPU.ReadPC().ToString("X8"));
            };

            debugger.DebugContinue += () => {
                m_System.DeviceCPU.Debugger.Continue();
                m_System.DeviceRcp.DeviceRsp.Debugger.Continue();
                Log.Info("CONTINUE...@" + m_System.DeviceCPU.ReadPC().ToString("X8"));
            };

            try
            {
                if (CoreConfig.Current.WorkbenchMode) {
                    while (!m_SignalExit)
                    {
                        if (m_Exception != null) throw m_Exception;

                        // Debugger break active, do not do anything
                        if (debugger.IsBreakActive) {

                            if (!paused) {
                                // TODO: Report RSP break
                                paused = true;
                            }

                            Thread.Sleep(100);

                            continue;
                        }

                        if (paused) {
                            paused = false;
                        }

                        // System core step
                        m_System.Tick();

                        // RSP step
                        // while (!m_System.DeviceRcp.DeviceRsp.IsHalted)
                        m_System.DeviceRcp.DeviceRsp.Step();

                        if (debugger.StepNext || debugger.StepRspNext) {
                            debugger.Break();
                        }

                        if (!debugger.IsBreakActive && m_System.DeviceCPU.Debugger.IsBreakActive) {
                            debugger.Break();
                        }

                        if (!debugger.IsBreakActive && m_System.DeviceRcp.DeviceRsp.Debugger.IsBreakActive) {
                            debugger.Break();
                        }
                    }
                }
                else if (s_Batch > 1) {
                    while (!m_SignalExit)
                    {
                        if (m_Exception != null) throw m_Exception;
                        if (m_SaveRequest != null || m_LoadRequest != null || AutoSave != null) ServiceStateRequests();
                        if (s_BenchVi > 0) BenchCheck();
                        m_System.TickBatch(s_Batch);
                    }
                }
                else {
                    while (!m_SignalExit)
                    {
                        if (m_Exception != null) throw m_Exception;

                        // System core step
                        m_System.Tick();

                        // RSP step
                        // while (!m_System.DeviceRcp.DeviceRsp.IsHalted)
                        m_System.DeviceRcp.DeviceRsp.Step();
                    }
                }

                m_ExitWait.Set();
            }
            catch (Exception e)
            {
                Log.Error(e);
                m_Exception = e;
            }
            finally
            {
                m_Running = false;
                m_StartWaitEvent.Reset();
                m_System.TickFinally();
                m_ExitWait.Set();
            }
        }

        // private void RspStart()
        // {
        //     StringBuilder errorMessage = new StringBuilder();

        //     m_RspThread = new Thread(() =>
        //     {
        //         Log.Debug("RSP Core Execution has started...");

        //         while (true)
        //         {
        //             try
        //             {
        //                 while (true)
        //                 {
        //                     m_System.DeviceRcp.DeviceRsp.Step();

        //                     #if DEBUG
        //                     // We must sleep some to allow other events be processed
        //                     // This is needed to allow the .NET debugger to function
        //                     if (m_System.DeviceRcp.DeviceRsp.IsHalted) {
        //                         Thread.Sleep(100);
        //                     }
        //                     #endif
        //                 }
        //             }
        //             catch (Exception e)
        //             {
        //                 // var lastInst = m_Rsp.LastReadInst;

        //                 // errorMessage.AppendLine(String.Format("Last inst read: 0x{0:X8} 0x{1:X8} {2}", 
        //                 //     lastInst.Address, lastInst.Inst.inst, m_System.DeviceRcp.DeviceRsp.Disassembler.GetFullDisassembly(lastInst)));

        //                 errorMessage.Append("Thrown exception: ").AppendLine(e.Message);

        //                 Log.Error(e.StackTrace.ToString());

        //                 Log.Error(errorMessage.ToString());

        //                 Log.Info("RSP will be halted now");

        //                 m_System.DeviceRcp.DeviceRsp.Halt();
        //             }
        //         }
        //     })
        //     {
        //         Name = "RSP Core Thread"
        //     };

        //     // m_RspThread.Start();
        // }

        public void Start()
        {
            var t = new Thread(new ThreadStart(RunLoop))
            {
                Name = "MIPS R4300I CPU Thread"
            };
            
            t.Start();

            //RspStart();

            if (!m_Running)
                m_StartWaitEvent.WaitOne();
        }

        public void Interrupt()
        {
            m_Exception = new Exception("The system was interrupted");
        }

        public void Resume()
        {
            m_Exception = null;
            Start();
        }

        public bool IsRunning => m_Running;

        public Exception ThrownException => m_Exception;
    }
}
