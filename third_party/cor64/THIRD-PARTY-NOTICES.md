# Third-party notices

Parts of cor64 are ported from ares (https://github.com/ares-emulator/ares), n64 core:

- RSP vector loads and stores (lbv lsv llv ldv lqv lrv lpv luv lhv, sbv ssv slv sdv sqv srv spv suv shv), VCH, VMRG flag handling
  (`src/cor64/Mips/Rsp/RspInterpreter.cs`)
- SP DMA row/skip semantics (`src/cor64/RCP/SPInterface.cs`)
- Audio interface DMA FIFO and interrupt model (`src/cor64/RCP/Audio.cs`)

ares license:

```
Copyright (c) 2004-2025 ares team, Near et al

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

paraLLEl-RDP (https://github.com/Themaister/parallel-rdp-standalone, commit 388d70f, vendored in `native/parallel-rdp-standalone`,
built with `native/prdp` into `cor64_prdp.dll`) is MIT licensed; its LICENSE file is kept in that folder:

```
Copyright (c) 2020 Themaister
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software
is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
```
Local change: `CommandProcessor::end_write_tmem()` added (rdp_device.hpp/.cpp, marked [cer64plus]) so savestates can restore TMEM.
(Abbreviated; see native/parallel-rdp-standalone/LICENSE for the full text. Bundled volk and Vulkan-Headers keep their own
licenses in their folders.)

Not used: mupen64plus-rsp-hle (GPL-2.0) and Project64 (GPL-2.0) are kept in `reference/` for reading only.
Porting their code into this MIT project would change its licensing terms.

## cen64 (opcode tables, cross-check only)
src/Tests/DebugDisasmTests.cs transcribes the VR4300 SPECIAL / REGIMM / primary opcode tables from cen64
(vr4300/decoder.c, Copyright (c) 2015 Tyler J. Stachecki, BSD 3-Clause licence) to cross-check src/cor64/Debug.
No cen64 code is used in the emulator.
