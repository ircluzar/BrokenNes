namespace NesEmulator
{
	/// <summary>
	/// Optional APU capability: predict how many CPU cycles remain until the DMC's next sample
	/// fetch takes the bus away from the CPU.
	///
	/// This exists so NES.RunFrame can arm its precise-DMA window *only* for the handful of
	/// instructions where a fetch is actually imminent, instead of paying per-cycle interleaving
	/// costs everywhere. Deliberately a separate interface rather than a new IAPU member: only
	/// APU_FIX implements it (per the FIX-core convention), and a core that doesn't implement it
	/// simply never arms the precise path, leaving its timing behavior bit-for-bit unchanged.
	/// </summary>
	public interface IDmcDmaSchedulable
	{
		/// <summary>
		/// CPU cycles until the next DMC sample fetch, 0 if one is due immediately, or
		/// int.MaxValue if no fetch is pending (DMC disabled or sample exhausted).
		/// </summary>
		int CyclesUntilDmcFetch();
	}
}
