// The allocation meter (2026-10-23): every "0 bytes allocated on the audio thread" check of this bench reads the DLL runtime's GC.GetAllocatedBytesForCurrentThread() before and after a
// window. That counter is alloc_bytes - (alloc_limit - alloc_ptr) of the thread's allocation context, and it is NOT exact across a garbage collection: when a collection that ANOTHER
// thread's allocations started retires a context that still has unused room (the rest of the 8 KB quantum the thread got at its last allocation), that room can come out counted as
// allocated by the thread, although it allocated nothing. Measured in the DLL (`reload` scenario `meter`, and the probe that found it): a thread that makes one small allocation and
// then runs allocation-free code while another thread builds NES sound chips (what the track offer's worker does for every NES track engine) saw its counter move in about 2% of
// 3 ms rounds, by up to 8,152 bytes; with its context emptied first (a collection on that thread, nothing allocated after) 0 of 3,000 rounds moved under the same ~3,800 gen-2
// collections. That is what the `reload` check reported once in 11 full runs ("6,400 bytes allocated on the audio thread" in the first block a channel of the second NES pass
// played, right after it took its track's engine and the worker began building the next one), and record-nes's unexplained 7,232 bytes of 2026-10-19: the plugin allocated
// nothing; the meter counted the room the test's own setup had left in the thread's context. (The original report counted 0 collections inside its window, as 2 of the 10
// reproduced ones did: the collection that retires the room need not be one counted inside the window.)
// So every window begins with AllocStart: a collection on THIS thread empties its context (hook 812), then the counter is read. The window must not allocate on this thread (that
// is what it checks), so the context stays empty and no collection can move the counter; a real allocation still counts in full (the context it takes then has room again, so the
// figure may be larger than the allocation, never smaller, and never 0). No tolerance, nothing suppressed.
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkAllocStart = 812, HkGcLoad = 813, HkAllocHere = 814;

    /// <summary>The deliberate bug of the bench itself (`reload` scenario `meter`): windows begin the old way, with whatever room the thread's allocation context has left.</summary>
    private static bool meterOldStart;

    /// <summary>The start of an allocation window on this thread: the DLL runtime's counter (any of hooks 4, 306, 349, 656, 847: they all read GC.GetAllocatedBytesForCurrentThread),
    /// read after this thread's allocation context was emptied by a collection on it (see the file's head). Read the end with the same hooks, allocating nothing in the DLL between.</summary>
    private static long AllocStart(PluginInstance p) => meterOldStart ? p.Test(HkRAlloc) : p.Test(HkAllocStart);

    /// <summary>The same for the bench's own (managed) counter, when a window measures both.</summary>
    private static long HostAllocStart()
    {
        if (!meterOldStart) GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        return GC.GetAllocatedBytesForCurrentThread();
    }
}
