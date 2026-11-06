FreeDOS 1.4 boot disk shipped with BrokenNes 2 (the x86 console)
=================================================================

What this folder is
-------------------
FD14LITE.img is the FreeDOS 1.4 "LiteUSB" disk image, exactly as the FreeDOS
project distributes it (inside FD14-LiteUSB.zip, from
https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/distributions/1.4/ ;
the zip's SHA-256 is 857DCD2EBF9D3D094320154DB5FB5B830ACBA6FB98F981A95A0CA7AB3350338B).
It is not modified. When you open a DOS program in BrokenNes 2, the app makes a
copy of this image in memory, writes its own FDCONFIG.SYS / FDAUTO.BAT and the
program onto the copy, and boots it on the x86 console. That copy (with whatever
the program saved) is kept as the game's save in your saves folder.

FreeDOS is a separate program, aggregated with BrokenNes 2 on the same medium.
It is not part of BrokenNes 2 and is not linked into it: BrokenNes 2 runs it
inside its emulated PC, the way a PC runs an operating system from a disk.

Licences
--------
FreeDOS is made of many programs by many authors. The kernel (KERNEL.SYS), the
shell (COMMAND.COM, FreeCOM) and most of the programs in \FREEDOS\BIN are free
software under the GNU General Public License, version 2 (or, for some, any
later version); the text of GPL version 2 is in GPL-2.0.txt beside this file.
Some programs are under other free licences (BSD-style, MIT, public domain and
others); each program's licence is stated in its FreeDOS package
(\PACKAGES\...\*.ZIP, file APPINFO\*.LSM) and on https://www.freedos.org/ .
BrokenNes 2's own licence (LICENSE.txt) does not apply to FreeDOS.

Source code (GPL, section 3(a))
-------------------------------
The complete corresponding source code is included: the folder source\ beside
this file holds the FreeDOS 1.4 package of every program on FD14LITE.img,
unmodified, each with its source in SOURCE\ (the 65 packages stored in the
image's \PACKAGES, plus grep, doslfn, srdisk and cdrcache, which are installed
in \FREEDOS\BIN and taken from the FreeDOS 1.4 LegacyCD of the same release).
source\SOURCES.txt lists every file with its SHA-256 and where it came from.
The installer's batch files on the image are their own source. The FreeDOS
project also publishes all of it at https://www.freedos.org/download/ and
https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/ .
