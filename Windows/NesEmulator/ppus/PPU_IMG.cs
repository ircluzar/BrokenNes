using System;

namespace NesEmulator
{
	/// <summary>
	/// Imagine-enabled PPU based on PPU_LOW with scanline capture hooks.
	/// Adds a low-overhead callback mechanism for CPU state capture during frame processing.
	/// </summary>
	public class PPU_IMG : PPU_LOW
	{
		// Core metadata
		public override string CoreName => "Imagine";
		public override string Description => "Low Power core with Imagine targeting hooks for scanline-specific corruption";
		public override int Performance => 15;
		public override int Rating => 4;
		public override string Category => "Debug";

		private ImagineTargetConfig? _targetConfig;
		private Action<ImagineCaptureData>? _captureCallback;
		private int _capturesThisFrame = 0;
		private const int MaxCapturesPerFrame = 32;

		public PPU_IMG(Bus bus) : base(bus)
		{
		}

		/// <summary>
		/// Configure Imagine targeting parameters.
		/// </summary>
		public void SetImagineTarget(ImagineTargetConfig? config, Action<ImagineCaptureData>? callback)
		{
			_targetConfig = config;
			_captureCallback = callback;
		}

		/// <summary>
		/// Clear targeting configuration (restore normal operation).
		/// </summary>
		public void ClearImagineTarget()
		{
			_targetConfig = null;
			_captureCallback = null;
			_capturesThisFrame = 0;
		}

		/// <summary>
		/// Reset per-frame counters (call at frame start).
		/// </summary>
		public void ResetFrameCaptures()
		{
			_capturesThisFrame = 0;
		}

		protected override void OnScanlineAdvanced(int currentScanline)
		{
			if (_targetConfig == null || _captureCallback == null) return;
			if (!_targetConfig.Enabled) return;
			if (_capturesThisFrame >= MaxCapturesPerFrame) return;

			bool shouldCapture = _targetConfig.Mode switch
			{
				ImagineTargetMode.SingleScanline => currentScanline == _targetConfig.TargetScanline,
				ImagineTargetMode.ScanlineRange => currentScanline >= _targetConfig.RangeStart && currentScanline <= _targetConfig.RangeEnd,
				ImagineTargetMode.ActiveRender => currentScanline >= 0 && currentScanline <= 239,
				ImagineTargetMode.VBlankPeriod => currentScanline >= 241 && currentScanline <= 261,
				ImagineTargetMode.FullFrame => true,
				_ => false
			};

			if (!shouldCapture) return;

			try
			{
				var capture = new ImagineCaptureData
				{
					Scanline = currentScanline,
					FramePhase = DetermineFramePhase(currentScanline),
					Timestamp = DateTime.UtcNow
				};

				_captureCallback(capture);
				_capturesThisFrame++;
			}
			catch { }
		}

		private static FramePhase DetermineFramePhase(int scanline)
		{
			if (scanline >= 0 && scanline <= 239) return FramePhase.ActiveRender;
			if (scanline == 240) return FramePhase.PostRender;
			if (scanline >= 241 && scanline <= 260) return FramePhase.VBlank;
			if (scanline == 261) return FramePhase.PreRender;
			return FramePhase.Unknown;
		}
	}

	/// <summary>
	/// Configuration for Imagine targeting.
	/// </summary>
	public class ImagineTargetConfig
	{
		public ImagineTargetMode Mode { get; set; } = ImagineTargetMode.InterFrame;
		public int TargetScanline { get; set; } = 120; // Default: middle of screen
		public int RangeStart { get; set; } = 0;
		public int RangeEnd { get; set; } = 239;
		public bool Enabled { get; set; } = false;
	}

	public enum ImagineTargetMode
	{
		InterFrame,
		SingleScanline,
		ScanlineRange,
		ActiveRender,
		VBlankPeriod,
		FullFrame
	}

	/// <summary>
	/// Data captured at target scanline.
	/// </summary>
	public class ImagineCaptureData
	{
		public int Scanline { get; set; }
		public FramePhase FramePhase { get; set; }
		public DateTime Timestamp { get; set; }

		// CPU state (populated by NES layer)
		public ushort PC { get; set; }
		public byte A { get; set; }
		public byte X { get; set; }
		public byte Y { get; set; }
		public byte P { get; set; }
		public ushort SP { get; set; }
	}

	public enum FramePhase
	{
		Unknown,
		ActiveRender,
		PostRender,
		VBlank,
		PreRender
	}
}
