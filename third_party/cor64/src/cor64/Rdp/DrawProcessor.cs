using System.Threading;
using System.IO;
using System;
using System.IO;
using cor64.RCP;
using NLog;

using CMD = cor64.Rdp.RdpCommandTypes;
using static cor64.RCP.DPCInterface;
using cor64.IO;
using cor64.Mips;

// TODO: RDP Command interface with DMA engine

namespace cor64.Rdp {
    public abstract class DrawProcessor : cor64.Cores.IN64PpuCore {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private DPCInterface m_Interface;
        private MipsInterface m_RcpInterface;
        private N64MemoryController m_Memory;
        private DisplayListReader m_DpReader;
        private bool m_DebugDL;
        private Video m_VideoInterface;

        public class TexLoadArgs : EventArgs {
            public uint RdramAddress { get; }
            public int Size { get; }
            public int Index { get; }

            internal TexLoadArgs(uint address, int index, int size) {
                RdramAddress = address;
                Size = size;
                Index = index;
            }
        }

        public event EventHandler<TexLoadArgs> Load;

        protected readonly RdpCommandTypeFactory.CallTable CallTable = RdpCommandTypeFactory.CreateCallTable();
        
        public abstract String Description { get; }

        protected MipsInterface RcpInterface => m_RcpInterface;

        // [cer64plus] for renderers that bypass the managed command decoder (ParallelRdp)
        protected DPCInterface DpcInterface => m_Interface;
        protected N64MemoryController Memory => m_Memory;

        protected Video VideoInterface => m_VideoInterface;

        private static readonly bool s_AudRdp1NullNoop = Environment.GetEnvironmentVariable("COR64_AUDRDP1_RDPNOP") != "0";
        private static bool s_AudRdp1NullLogged;

        protected DrawProcessor() {
            CallTable
            .Map(LoadBlock, CMD.LoadBlock)
            .Map(LoadTile, CMD.LoadTile)
            .Map(LoadTlut, CMD.LoadTLUT)
            .Map(Rectangle, CMD.FillRect, CMD.TexRect, CMD.TexRectFlip)
            .Map(SetImage, CMD.SetColorImage, CMD.SetMaskImage, CMD.SetTexImage)
            .Map(SetColor, CMD.SetBlendColor, CMD.SetEnvColor, CMD.SetPrimColor, CMD.SetFogColor, CMD.SetFillColor)
            .Map(Triangle, CMD.TriNoShade, CMD.TriZNoShade, CMD.TriTex, CMD.TriZTex, CMD.TriShade, CMD.TriZShade, CMD.TriTexShade, CMD.TriZTexShade)
            .Map(SetKey, CMD.SetKeyRed, CMD.SetKeyGB)
            .Map(SetTileSize, CMD.SetTileSize)
            .Map(SetTile, CMD.SetTile)
            .Map(SetCombineMode, CMD.SetCombine)
            .Map(SetOtherModes, CMD.SetOtherModes)
            .Map(SetPrimDepth, CMD.SetPrimDepth)
            .Map(SetSync, CMD.SyncFull, CMD.SyncLoad, CMD.SyncPipe, CMD.SyncTile)
            .Map(SetScissor, CMD.SetScissor)
            .Map(SetConvert, CMD.SetConvert)
            .Map(DoNothing, CMD.Noop)
            .Finish();
        }

        /// <summary>[cer64plus] Wait until all rendering already submitted has landed in RDRAM (state boundaries). No-op for CPU renderers.</summary>
        public virtual void Drain() { }
        /// <summary>[cer64plus] Copy renderer-side state that is not in RDRAM into saved fields (default: just drain).</summary>
        public virtual void BeforeStateSave() => Drain();
        /// <summary>[cer64plus] Push restored fields back to the renderer after a load.</summary>
        public virtual void AfterStateLoad() { }

        // [cer64plus] PPU slot contract
        public virtual string Id => GetType().Name;
        public virtual void BeforeScanout() { }
        public virtual bool ScanOut(Video vi, ref uint[] frame, out int width, out int height) => cor64.Session.FrameCapture.Capture(vi, ref frame, out width, out height);
        public virtual bool TryHandleTask(in cor64.Cores.N64Task task) => false;
        public virtual void Dispose() { }

        public void DetachInterface() {
            if (m_Interface != null) m_Interface.DisplayListReady -= DisplayListHandler;
        }

        public virtual void Init() {
        }

        public void AttachInterface(MipsInterface rcpInterface, DPCInterface iface, Video videoInterface) {
            m_RcpInterface = rcpInterface;
            m_Interface = iface;
            m_Interface.DisplayListReady += DisplayListHandler;
            m_VideoInterface = videoInterface;
        }

        public virtual void AttachMemory(N64MemoryController memory) {
            m_Memory = memory;
            m_DpReader = new DisplayListReader(memory);
        }

        internal void NotifyTextureLoad(TexLoadArgs args) {
            Load?.Invoke(this, args);
        }

        public void ReadTexture(uint rdramAddress, byte[] buffer, int offset, int count) {
            for (int i = 0; i < count; i++) {
                buffer[offset + i] = m_Memory.U8(rdramAddress + i);
            }
        }

        protected virtual void DisplayListHandler(object sender, DPCInterface.DisplayList displayList) {
            int size = (int)(displayList.End - displayList.Start);

            // cor64.Debugging.Debugger.Current.TurnOnCpuLogging();

            // Log.Debug("Display List: {0:X8}, Size {1}", displayList.Start, size);

            // Print out the display list bytes
            // m_IMemory.Position = displayList.Start;
            // Console.Write("DP: ");
            // for (int i = 0; i < size; i++)  {
            //     Console.Write("{0:X2}", m_IMemory.ReadByte());
            // }
            // Console.WriteLine();

            long address = displayList.Start;

            // Log.Debug("Reading display list from {0}", m_Interface.UseXBus ? "xbus" : "rdram");

            /* Now read memory of the display list */
            var commands = m_DpReader.ReadDisplayList(address, size);

            // RDP is frozen, sleep
            while ((m_Interface.RFlags & ReadStatusFlags.Freeze) == ReadStatusFlags.Freeze) {
                // Signal RDP profiler start (RDP is frozen ready for profiling)
                m_Interface.RFlags |= ReadStatusFlags.StartGClk;

                Log.Debug("RDP is frozen");
                Thread.Sleep(1);
            }

            // Clear RDP profiler start (FROZEN status)
            m_Interface.RFlags |= ReadStatusFlags.StartGClk;

            // Mark the start, end valid bits invalid
            m_Interface.RFlags |= ReadStatusFlags.StartValid;
            m_Interface.RFlags |= ReadStatusFlags.EndValid;

            // Indicate the RDP pipeline is busy
            m_Interface.RFlags |= ReadStatusFlags.PipeBusy;

            // TODO: TMEM busy should be set specificly when loading data into TMEM

            for (int i = 0; i < commands.Count; i++) {
                var command = commands[i];

                // if (i == 18) {
                //     break;
                // }

                // if (command.ResolveType().Type.Id == RdpCommandTypes.TriTex.Id) {
                //     if (i != 35 /*&& i != 36*/) {
                //         continue;
                //     }
                // }

                // string cmdLabel = command.Type.Name.ToLower();
                // cmdLabel += "_";
                // cmdLabel += i.ToString();
                // command.PrintCommandCArray(cmdLabel);

                var resolvedCommand = command.ResolveType();

                #if DEBUG_RDP_TRI_COMMANDS && !DEBUG_RDP_COMMANDS
                if (resolvedCommand.Type.Id == CMD.TriNoShade.Id ||
                    resolvedCommand.Type.Id == CMD.TriShade.Id ||
                    resolvedCommand.Type.Id == CMD.TriTex.Id ||
                    resolvedCommand.Type.Id == CMD.TriTexShade.Id ||
                    resolvedCommand.Type.Id == CMD.TriZNoShade.Id ||
                    resolvedCommand.Type.Id == CMD.TriZShade.Id ||
                    resolvedCommand.Type.Id == CMD.TriZTex.Id ||
                    resolvedCommand.Type.Id == CMD.TriZTexShade.Id) {
                    Log.Debug("{0}", resolvedCommand.ToString());
                }
                #endif

                #if DEBUG_RDP_COMMANDS
                Log.Debug("RDP_CMD: {0:X8} {1}", address, resolvedCommand.ToString());
                #endif

                if (Telemetry.CaptureRdp > 0) { Telemetry.CaptureRdp--; if (EventLog.Enabled) EventLog.E("RDP", resolvedCommand.ToString().Replace(System.Environment.NewLine, " | ")); }
                Telemetry.RdpCmd.AddOrUpdate(resolvedCommand.Type.Name, 1, (_, c) => c + 1);
                address += resolvedCommand.Type.Size;

                // Set command busy
                m_Interface.RFlags |= ReadStatusFlags.CmdBusy;

                var handler = CallTable[command];

                // Clear command busy
                m_Interface.RFlags &= ~ReadStatusFlags.CmdBusy;

                if (handler != null)
                    handler?.Invoke(command);
                else if (s_AudRdp1NullNoop) {
                    // [audio_rdp1] COR64_AUDRDP1_RDPNOP=1 (default on): an RDP opcode with no handler (invalid / reserved
                    // ids) is a no-op on hardware (parallel-rdp ignores it too); it used to throw (Dezaemon 3D, frame 3826).
                    if (!s_AudRdp1NullLogged) { s_AudRdp1NullLogged = true; if (Environment.GetEnvironmentVariable("COR64_AUDRDP1_LLEIDX_LOG") == "1") Console.WriteLine($"[audrdp1] RDP no handler: {resolvedCommand.Type.Name}"); }
                }
                else
                    throw new InvalidOperationException("RDP command handler is null");
            }

            // Clear the RDP pipeline busy status
            m_Interface.RFlags &= ~ReadStatusFlags.PipeBusy;

            // Done reading the display, we can clear these bits
            m_Interface.RFlags &= ~ReadStatusFlags.StartValid;
            m_Interface.RFlags &= ~ReadStatusFlags.EndValid;

            // Indicate that the RDP command buffer is ready to be read
            m_Interface.RFlags |= ReadStatusFlags.CommandBufferReady;

            // Log.Debug("RDP finished");
        }

        public void SetDLDebug(bool enable) {
            m_DebugDL = enable;
        }

        protected static void DoNothing(RdpCommand command) {
            return;
        }

        protected abstract void LoadBlock(RdpCommand command);

        protected abstract void LoadTile(RdpCommand command);

        protected abstract void LoadTlut(RdpCommand command);

        protected abstract void Rectangle(RdpCommand command);

        protected abstract void SetCombineMode(RdpCommand command);

        protected abstract void SetConvert(RdpCommand command);

        protected abstract void SetKey(RdpCommand command);

        protected abstract void SetOtherModes(RdpCommand command);

        protected abstract void SetColor(RdpCommand command);

        protected abstract void SetPrimDepth(RdpCommand command);

        protected abstract void SetScissor(RdpCommand command);

        protected abstract void SetTile(RdpCommand command);

        protected abstract void SetTileSize(RdpCommand command);

        protected abstract void SetImage(RdpCommand command);

        protected virtual void SetSync(RdpCommand command) {
            bool l = (command.Type.Flags & RdpCommandFlags.Load) == RdpCommandFlags.Load;
            bool p = (command.Type.Flags & RdpCommandFlags.Pipeline) == RdpCommandFlags.Pipeline;
            bool t = (command.Type.Flags & RdpCommandFlags.Tile) == RdpCommandFlags.Tile;

            if (l && p && t) {
                RcpInterface.SetInterrupt(MipsInterface.INT_DP, true);
            }
        }

        protected abstract void Triangle(RdpCommand command);
    }
}