// [cer64plus] Minimal C API over parallel-rdp (MIT, Themaister) for cor64.
// cor64 owns RDRAM; it is imported as Vulkan host memory, so the GPU renders straight into the emulator's RDRAM and the
// existing VI scanout path keeps working. Synchronisation is explicit: prdp_sync_full() makes RDRAM coherent.
#include "context.hpp"
#include "device.hpp"
#include "rdp_device.hpp"
#include <memory>
#include <cstdint>
#include <cstdio>
#include <cstring>

using namespace Vulkan;
using namespace RDP;

struct PrdpInstance
{
	std::unique_ptr<Context> context;
	std::unique_ptr<Device> device;
	std::unique_ptr<CommandProcessor> processor;
	void *rdram; uint32_t rdram_size, hidden_size, flags;
};

extern "C" {

// Returns null on failure (no Vulkan, or the device cannot import host memory). rdram must be 64 KB aligned.
__declspec(dllexport) void *prdp_create(void *rdram, uint32_t rdram_size, uint32_t hidden_rdram_size, uint32_t flags)
{
	auto inst = std::make_unique<PrdpInstance>();
	if (!Context::init_loader(nullptr)) { fprintf(stderr, "prdp: Vulkan loader not found\n"); return nullptr; }
	inst->context = std::make_unique<Context>();
	if (!inst->context->init_instance_and_device(nullptr, 0, nullptr, 0)) { fprintf(stderr, "prdp: no Vulkan device\n"); return nullptr; }
	inst->device = std::make_unique<Device>();
	inst->device->set_context(*inst->context);
	inst->rdram = rdram; inst->rdram_size = rdram_size; inst->hidden_size = hidden_rdram_size; inst->flags = flags;
	inst->processor = std::make_unique<CommandProcessor>(*inst->device, rdram, 0, rdram_size, hidden_rdram_size, flags);
	if (!inst->processor->device_is_supported()) { fprintf(stderr, "prdp: device does not support parallel-rdp (host memory import?)\n"); return nullptr; }
	return inst.release();
}

__declspec(dllexport) void prdp_enqueue(void *handle, uint32_t num_words, const uint32_t *words)
{
	static_cast<PrdpInstance *>(handle)->processor->enqueue_command(num_words, words);
}

// [cer64plus] Several commands packed as {num_words, words...}; same ring contents as one prdp_enqueue per command.
__declspec(dllexport) void prdp_enqueue_batch(void *handle, uint32_t total_words, const uint32_t *packed)
{
	static_cast<PrdpInstance *>(handle)->processor->enqueue_commands(total_words, packed);
}

// Waits until every enqueued command has been rendered into RDRAM.
__declspec(dllexport) void prdp_sync_full(void *handle)
{
	auto *p = static_cast<PrdpInstance *>(handle)->processor.get();
	p->wait_for_timeline(p->signal_timeline());
}

// Submits everything enqueued so far and returns a timeline token; prdp_wait(token) blocks until it has finished.
__declspec(dllexport) uint64_t prdp_signal(void *handle)
{
	return static_cast<PrdpInstance *>(handle)->processor->signal_timeline();
}

__declspec(dllexport) void prdp_wait(void *handle, uint64_t token)
{
	static_cast<PrdpInstance *>(handle)->processor->wait_for_timeline(token);
}

__declspec(dllexport) void prdp_begin_frame(void *handle)
{
	static_cast<PrdpInstance *>(handle)->processor->begin_frame_context();
}

// [keys_leak] Same as prdp_begin_frame, kept under its own name for the managed side's periodic call. Granite only frees
// a frame context's deferred resources (destroyed buffers/images, staging blocks, command pools, fences) when the device
// moves to the next frame context. cor64 never called it, so everything parallel-rdp released piled up for the whole run
// (mtennis: 12 GB of LinkedDeviceHost memory after ~19000 frames).
__declspec(dllexport) void prdp_next_frame(void *handle)
{
	static_cast<PrdpInstance *>(handle)->processor->begin_frame_context();
}

// Throws away all GPU-side RDP state (TMEM, caches, pipelines' state) by recreating the command processor on the same
// device. Used after a savestate load so nothing from the previous timeline leaks into the restored one.
__declspec(dllexport) int prdp_reset(void *handle)
{
	auto *inst = static_cast<PrdpInstance *>(handle);
	inst->processor->idle();
	inst->processor.reset();
	inst->processor = std::make_unique<CommandProcessor>(*inst->device, inst->rdram, 0, inst->rdram_size, inst->hidden_size, inst->flags);
	return inst->processor->device_is_supported() ? 1 : 0;
}

// Hidden RDRAM (the 9th-bit coverage/AA memory) lives only on the GPU side; savestates carry it through these.
__declspec(dllexport) uint32_t prdp_hidden_size(void *handle)
{
	return (uint32_t)static_cast<PrdpInstance *>(handle)->processor->get_hidden_rdram_size();
}

__declspec(dllexport) void prdp_read_hidden(void *handle, void *dst, uint32_t size)
{
	auto *p = static_cast<PrdpInstance *>(handle)->processor.get();
	p->idle();
	size_t n = p->get_hidden_rdram_size(); if (size < n) n = size;
	void *src = p->begin_read_hidden_rdram(); // null unless created with HOST_VISIBLE_HIDDEN_RDRAM
	if (src) memcpy(dst, src, n); else memset(dst, 0, n);
}

__declspec(dllexport) void prdp_write_hidden(void *handle, const void *src, uint32_t size)
{
	auto *p = static_cast<PrdpInstance *>(handle)->processor.get();
	p->idle();
	size_t n = p->get_hidden_rdram_size(); if (size < n) n = size;
	void *dst = p->begin_read_hidden_rdram();
	if (!dst) return;
	memcpy(dst, src, n);
	p->end_write_hidden_rdram();
}

// TMEM (4 KB), host-visible when created with flag bit 1.
__declspec(dllexport) void prdp_read_tmem(void *handle, void *dst)
{
	auto *p = static_cast<PrdpInstance *>(handle)->processor.get();
	p->idle();
	void *src = p->get_tmem();
	if (src) memcpy(dst, src, 0x1000); else memset(dst, 0, 0x1000);
}

__declspec(dllexport) void prdp_write_tmem(void *handle, const void *src)
{
	auto *p = static_cast<PrdpInstance *>(handle)->processor.get();
	p->idle();
	void *dst = p->get_tmem();
	if (!dst) return;
	memcpy(dst, src, 0x1000);
	p->end_write_tmem();
}

__declspec(dllexport) void prdp_destroy(void *handle)
{
	auto *inst = static_cast<PrdpInstance *>(handle);
	if (inst && inst->processor) inst->processor->idle();
	delete inst;
}

}
