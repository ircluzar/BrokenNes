using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NesEmulator
{
	/// <summary>
	/// Bounded per-instruction CPU trace for CPU_FIX only - the C# mirror of fceux_custom's
	/// nesreflex_trace.cpp/h, byte-identical record layout, so one lockstep diff tool reads both
	/// sides. Only writes while startFrame &lt;= currentFrame &lt;= endFrame (endFrame &lt; 0 means
	/// unbounded), so tracing a window around a known divergence point stays cheap even for a long
	/// movie. Not wired into any other CPU core - accuracy tooling targets CPU_FIX only (see the
	/// project_fix_core_family memory).
	///
	/// 24-byte record:
	///   0-3   frame        u32
	///   4-7   cycleInFrame u32  (CPU cycles elapsed since this frame started)
	///   8-9   PC           u16  (address of the opcode, before it executes)
	///   10    opcode       u8
	///   11    cycleCost    u8
	///   12    A   13 X   14 Y   15 S   16 P    (all pre-instruction)
	///   17    eventFlags   u8   (bit0 = NMI dispatched just before this instruction, bit1 = IRQ)
	///   18-23 reserved
	///
	/// cycleInFrame is accumulated here rather than read from NES.globalCpuCycle, so the exact
	/// same accounting rule applies on both emulators (FCEUX has no equivalent to expose) - it
	/// counts instruction cycle costs plus interrupt dispatch, and deliberately does NOT include
	/// DMA stalls. A drift between the two sides' counters is therefore itself a finding, not
	/// noise to explain away.
	/// </summary>
	public static class InstructionTracer
	{
		private const int RecordSize = 24;

		private static string? path;
		private static bool enabled;
		private static int startFrame;
		private static int endFrame; // -1 = unbounded
		private static int currentFrame;
		private static uint cycleInFrame;
		private static byte pendingEventFlags;
		private static FileStream? stream;
		private static readonly byte[] recordBuf = new byte[RecordSize];

		public static void Configure(string? tracePath, int startFrame, int endFrame)
		{
			Shutdown();
			path = tracePath;
			enabled = !string.IsNullOrEmpty(tracePath);
			InstructionTracer.startFrame = startFrame;
			InstructionTracer.endFrame = endFrame;
			currentFrame = 0;
			cycleInFrame = 0;
			pendingEventFlags = 0;
		}

		public static void OnFrameComplete()
		{
			currentFrame++;
			cycleInFrame = 0;
		}

		/// <summary>kind: 1 = NMI, 2 = IRQ. Mirrors NESReflexTrace_OnInterrupt.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void OnInterrupt(byte kind, byte cycles)
		{
			if (!enabled) return;
			cycleInFrame += cycles;
			if (kind == 1) pendingEventFlags |= 0x01;
			else if (kind == 2) pendingEventFlags |= 0x02;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void OnInstruction(ushort pc, byte opcode, byte a, byte x, byte y, byte sp, byte status, byte cycleCost)
		{
			if (!enabled) return;

			uint cyc = cycleInFrame;
			byte flags = pendingEventFlags;
			// Advance/clear even outside the window so cycleInFrame is already correct the moment
			// the window opens, rather than starting from a stale zero.
			cycleInFrame += cycleCost;
			pendingEventFlags = 0;

			if (currentFrame < startFrame) return;
			if (endFrame >= 0 && currentFrame > endFrame) return;

			EnsureOpen();
			if (stream == null) return;

			var buf = recordBuf;
			Array.Clear(buf, 0, RecordSize);
			uint f = (uint)currentFrame;
			buf[0] = (byte)(f & 0xFF);
			buf[1] = (byte)((f >> 8) & 0xFF);
			buf[2] = (byte)((f >> 16) & 0xFF);
			buf[3] = (byte)((f >> 24) & 0xFF);
			buf[4] = (byte)(cyc & 0xFF);
			buf[5] = (byte)((cyc >> 8) & 0xFF);
			buf[6] = (byte)((cyc >> 16) & 0xFF);
			buf[7] = (byte)((cyc >> 24) & 0xFF);
			buf[8] = (byte)(pc & 0xFF);
			buf[9] = (byte)((pc >> 8) & 0xFF);
			buf[10] = opcode;
			buf[11] = cycleCost;
			buf[12] = a;
			buf[13] = x;
			buf[14] = y;
			buf[15] = sp;
			buf[16] = status;
			buf[17] = flags;
			stream.Write(buf, 0, RecordSize);
		}

		private static void EnsureOpen()
		{
			if (stream != null || !enabled) return;
			stream = new FileStream(path!, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
		}

		public static void Shutdown()
		{
			stream?.Flush();
			stream?.Dispose();
			stream = null;
		}
	}
}
