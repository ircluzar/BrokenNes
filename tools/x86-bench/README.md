# x86 console test programs (compat catalog P6)

Differential tests for the x86 console: each DOS program runs headless on BrokenNes (`--x86boot` on a FreeDOS disk) and on DOSBox-X
(core=normal, cputype=486), and the outputs are diffed. `both.sh "CMD1" "CMD2" ...` (git bash) does it; it takes a heavy-job slot.

- `flagt.asm` (NASM, `nasm -f bin -o FLAGT.COM flagt.asm`): ALU / flags / bit-op table test. Each line is `op hash` over 21x21 operand pairs
  x 2 input flag states; only architecturally defined flags are hashed. `FLAGT name` prints every case of one op.
  Known, accepted differences vs DOSBox-X: cmpxchg8/16/32 (DOSBox-X sets no CMP flags; BrokenNes is right), shld16/shrd16
  (count 17-31 is undefined on the 386).

Results and the list of downloadable suites: see RESULTS.md.
