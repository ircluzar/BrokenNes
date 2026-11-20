/* Copyright (c) 2020 Themaister
 *
 * Permission is hereby granted, free of charge, to any person obtaining
 * a copy of this software and associated documentation files (the
 * "Software"), to deal in the Software without restriction, including
 * without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to
 * permit persons to whom the Software is furnished to do so, subject to
 * the following conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
 * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
 * CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
 * TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
 * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

#include <chrono>
#include "command_ring.hpp"
#include "rdp_device.hpp"
#include "thread_id.hpp"
#include <assert.h>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <windows.h>

namespace RDP
{
// [cer64plus] COR64_PRDPW_BATCH (default on, =0 off): the producer wakes the worker only for sync/flush commands,
// when the ring is a quarter full, or when it has to wait itself; the worker takes every queued command under one
// lock. The command stream the worker executes is unchanged; only how often it is woken.
static bool prdpw_batch()
{
	static int v = -1;
	if (v < 0) { const char *e = getenv("COR64_PRDPW_BATCH"); v = (e && e[0] == '0') ? 0 : 1; }
	return v != 0;
}

void CommandRing::init(
#ifdef PARALLEL_RDP_SHADER_DIR
		Granite::Global::GlobalManagersHandle global_handles_,
#endif
		CommandProcessor *processor_, unsigned count)
{
	assert((count & (count - 1)) == 0);
	teardown_thread();
	processor = processor_;
	batch = prdpw_batch();
	ring.resize(count);
	write_count = 0;
	read_count = 0;
#ifdef PARALLEL_RDP_SHADER_DIR
	global_handles = std::move(global_handles_);
#endif
	thr = std::thread(&CommandRing::thread_loop, this);
}

void CommandRing::teardown_thread()
{
	if (thr.joinable())
	{
		enqueue_command(0, nullptr);
		thr.join();
	}
}

CommandRing::~CommandRing()
{
	teardown_thread();
}

void CommandRing::drain()
{
	std::unique_lock<std::mutex> holder{lock};
	if (write_count != completed_count)
		cond.notify_one();
	cond.wait(holder, [this]() {
		return write_count == completed_count;
	});
}

void CommandRing::enqueue_command(unsigned num_words, const uint32_t *words)
{
	std::unique_lock<std::mutex> holder{lock};
	if (write_count + num_words + 1 > read_count + ring.size())
	{
		cond.notify_one();
		cond.wait(holder, [this, num_words]() {
			return write_count + num_words + 1 <= read_count + ring.size();
		});
	}

	size_t mask = ring.size() - 1;
	ring[write_count++ & mask] = num_words;
	for (unsigned i = 0; i < num_words; i++)
		ring[write_count++ & mask] = words[i];

	if (batch)
	{
		unsigned op = num_words ? (words[0] >> 24) & 63 : 0;
		bool urgent = num_words == 0 || op == unsigned(Op::SyncFull) || op == unsigned(Op::MetaSignalTimeline) ||
		              op == unsigned(Op::MetaFlush) || write_count - read_count >= ring.size() / 4;
		if (!urgent)
			return;
	}
	cond.notify_one();
}

void CommandRing::enqueue_commands(size_t total_words, const uint32_t *packed)
{
	std::unique_lock<std::mutex> holder{lock};
	size_t mask = ring.size() - 1;
	size_t pos = 0;
	while (pos < total_words)
	{
		uint32_t n = packed[pos];
		if (write_count + n + 1 > read_count + ring.size())
		{
			cond.notify_one();
			cond.wait(holder, [this, n]() { return write_count + n + 1 <= read_count + ring.size(); });
		}
		for (uint32_t i = 0; i <= n; i++)
			ring[write_count++ & mask] = packed[pos + i];
		pos += n + 1;
	}
	cond.notify_one();
}

void CommandRing::thread_loop()
{
	Util::register_thread_index(0);

#ifdef PARALLEL_RDP_SHADER_DIR
	// Here to let the RDP play nice with full Granite.
	// When we move to standalone Granite, we won't need to interact with global subsystems like this.
	Granite::Global::set_thread_context(*global_handles);
	global_handles.reset();
#endif

	// [cer64plus] COR64_PRDPW_PROF=1: per-op thread-cycle profile of this worker (measurement only).
	const char *pe = getenv("COR64_PRDPW_PROF");
	bool prof = pe && pe[0] == '1';
	uint64_t opcyc[66] = {}, opcnt[66] = {}, wakes = 0, signals = 0, t0c = 0;
	HANDLE self = GetCurrentThread();
	if (prof) QueryThreadCycleTime(self, &t0c);
	std::vector<uint32_t> tmp_buffer;
	tmp_buffer.reserve(64);
	size_t mask = ring.size() - 1;

	if (batch)
	{
		std::vector<uint32_t> local;
		local.reserve(ring.size());
		bool quit = false;
		while (!quit)
		{
			uint64_t snap = 0;
			bool is_idle = false;
			{
				std::unique_lock<std::mutex> holder{lock};
				if (cond.wait_for(holder, std::chrono::microseconds(500), [this]() { return write_count > read_count; }))
				{
					local.resize(size_t(write_count - read_count));
					for (size_t i = 0; i < local.size(); i++)
						local[i] = ring[read_count++ & mask];
					snap = read_count;
				}
				else
					is_idle = true;
			}
			if (is_idle)
			{
				const uint32_t idle_word = uint32_t(Op::MetaIdle) << 24;
				processor->enqueue_command_direct(1, &idle_word);
				continue;
			}
			// Free the ring space at once; completion is reported after execution.
			{
				std::lock_guard<std::mutex> holder{lock};
				cond.notify_one();
			}
			size_t pos = 0;
			while (pos < local.size())
			{
				uint32_t n = local[pos++];
				if (n == 0) { quit = true; break; }
				uint64_t c0 = 0;
				if (prof) QueryThreadCycleTime(self, &c0);
				processor->enqueue_command_direct(n, &local[pos]);
				if (prof)
				{
					uint64_t c1; QueryThreadCycleTime(self, &c1);
					unsigned op = (local[pos] >> 24) & 63;
					opcyc[op] += c1 - c0; opcnt[op]++;
				}
				pos += n;
			}
			wakes++;
			if (prof && opcnt[0x29] >= 100)
			{
				uint64_t tot = 0; QueryThreadCycleTime(self, &tot); tot -= t0c;
				uint64_t in = 0; for (int i = 0; i < 66; i++) in += opcyc[i];
				FILE *pf = fopen("prdpw_prof.txt", "a"); if (!pf) pf = stderr;
				fprintf(pf, "PRDPW batch total %.1f Mcyc, in-ops %.1f, outside %.1f, wakes %llu\n", tot / 1e6, in / 1e6, (tot - in) / 1e6, (unsigned long long)wakes);
				if (pf != stderr) fclose(pf);
				memset(opcyc, 0, sizeof(opcyc)); memset(opcnt, 0, sizeof(opcnt)); wakes = 0; QueryThreadCycleTime(self, &t0c);
			}
			{
				std::lock_guard<std::mutex> holder{lock};
				completed_count = snap;
				cond.notify_one();
			}
		}
		return;
	}

	for (;;)
	{
		bool is_idle = false;
		{
			std::unique_lock<std::mutex> holder{lock};
			if (cond.wait_for(holder, std::chrono::microseconds(500), [this]() { return write_count > read_count; }))
			{
				uint32_t num_words = ring[read_count++ & mask];
				tmp_buffer.resize(num_words);
				for (uint32_t i = 0; i < num_words; i++)
					tmp_buffer[i] = ring[read_count++ & mask];
			}
			else
			{
				// If we don't receive commands at a steady pace,
				// notify rendering thread that we should probably kick some work.
				tmp_buffer.resize(1);
				tmp_buffer[0] = uint32_t(Op::MetaIdle) << 24;
				is_idle = true;
			}
		}

		if (tmp_buffer.empty())
			break;

		uint64_t c0 = 0;
		if (prof) QueryThreadCycleTime(self, &c0);
		processor->enqueue_command_direct(tmp_buffer.size(), tmp_buffer.data());
		if (prof)
		{
			uint64_t c1; QueryThreadCycleTime(self, &c1);
			unsigned op = (tmp_buffer[0] >> 24) & 63;
			if (is_idle) op = 64;
			opcyc[op] += c1 - c0; opcnt[op]++;
			if (op == 0x29 || (tmp_buffer[0] >> 24) == unsigned(Op::MetaSignalTimeline)) signals++;
			if (signals >= 200)
			{
				signals = 0;
				uint64_t tot = 0; QueryThreadCycleTime(self, &tot); tot -= t0c;
				uint64_t in = 0; for (int i = 0; i < 66; i++) in += opcyc[i];
				FILE *pf = fopen("prdpw_prof.txt", "a"); if (!pf) pf = stderr;
				fprintf(pf, "PRDPW total %.1f Mcyc, in-ops %.1f, outside(lock/wait) %.1f\n", tot / 1e6, in / 1e6, (tot - in) / 1e6);
				for (int i = 0; i < 66; i++) if (opcnt[i]) fprintf(pf, "PRDPW op %02x n=%llu cyc=%.2fM avg=%.0f\n", i, (unsigned long long)opcnt[i], opcyc[i] / 1e6, double(opcyc[i]) / opcnt[i]);
				if (pf != stderr) fclose(pf);
				memset(opcyc, 0, sizeof(opcyc)); memset(opcnt, 0, sizeof(opcnt)); QueryThreadCycleTime(self, &t0c);
			}
		}
		if (!is_idle)
		{
			std::lock_guard<std::mutex> holder{lock};
			completed_count = read_count;
			cond.notify_one();
		}
	}
}
}
