; cpuvec.asm: x87 FPU and MMX test vectors for BrokenNes's CPU models (Workshop --x86cpu). Our own code.
; Raw binary, loaded at 1000:0000 (CS = DS = ES = SS = 1000h), real mode. Output to port E9h, one line per check:
; "ok <name>" or "FAIL <name> <got>". Expected values were worked out by hand (IEEE / Intel SDM semantics).
; The FPU part runs when FNINIT / FNSTSW finds an FPU, the MMX part when CPUID reports MMX. Ends with "DONE" and HLT.
; Build: nasm -f bin cpuvec.asm -o cpuvec.bin
        bits 16
        org 0
start:
        mov sp, 0xfff0
        cld
        mov word [tmpw], 0x5a5a
        fninit
        fnstsw [tmpw]
        cmp word [tmpw], 0
        je .fpu
        mov si, s_nofpu
        call puts
        jmp mmx_probe
.fpu:   mov si, s_fpu
        call puts
        call fpu_tests
mmx_probe:
        pushfd
        pop eax
        mov ecx, eax
        xor eax, 0x200000
        push eax
        popfd
        pushfd
        pop eax
        push ecx
        popfd
        xor eax, ecx
        test eax, 0x200000
        jz .nommx
        mov eax, 1
        cpuid
        test edx, 0x800000
        jz .nommx
        mov si, s_mmx
        call puts
        call mmx_tests
        jmp done
.nommx: mov si, s_nommx
        call puts
done:   mov si, s_done
        call puts
        hlt
        jmp done

; ---------------- helpers ----------------
puts:   lodsb
        test al, al
        jz .r
        out 0xe9, al
        jmp puts
.r:     ret

hex8:   push ax
        shr al, 4
        call .nib
        pop ax
        push ax
        and al, 15
        call .nib
        pop ax
        ret
.nib:   add al, 0x30
        cmp al, 0x39
        jbe .o
        add al, 7
.o:     out 0xe9, al
        ret

; compare cx bytes at si (got) with di (expected); bx = name
check:  push si
        push di
        push cx
        repe cmpsb
        pop cx
        pop di
        pop si
        jne .bad
        mov si, s_ok
        call puts
        mov si, bx
        call puts
        mov si, s_nl
        call puts
        ret
.bad:   push si
        mov si, s_fail
        call puts
        mov si, bx
        call puts
        mov al, 0x20
        out 0xe9, al
        pop si
        add si, cx
.l:     dec si
        mov al, [si]
        call hex8
        loop .l
        mov si, s_nl
        call puts
        ret

%macro CHK64 3
        mov bx, %%n
        mov si, %2
        mov di, %%e
        mov cx, 8
        call check
        jmp %%x
%%n:    db %1, 0
%%e:    dq %3
%%x:
%endmacro
%macro CHK32 3
        mov bx, %%n
        mov si, %2
        mov di, %%e
        mov cx, 4
        call check
        jmp %%x
%%n:    db %1, 0
%%e:    dd %3
%%x:
%endmacro
%macro CHK16 3
        mov bx, %%n
        mov si, %2
        mov di, %%e
        mov cx, 2
        call check
        jmp %%x
%%n:    db %1, 0
%%e:    dw %3
%%x:
%endmacro

; ---------------- x87 ----------------
fpu_tests:
        fninit
        fnstsw [v16]
        CHK16 "fninit.sw", v16, 0
        fnstcw [v16]
        CHK16 "fninit.cw", v16, 0x037f
        fld1
        fld1
        faddp st1, st0
        fistp dword [v32]
        CHK32 "1+1", v32, 2
        fldpi
        fstp qword [v64]
        CHK64 "fldpi", v64, 0x400921FB54442D18
        fild qword [big]
        fistp qword [v64]
        CHK64 "fild/fistp m64 exact", v64, 0x123456789ABCDEF1
        fninit
        fld1
        fldz
        fdivp st1, st0
        fstp qword [v64]
        CHK64 "1/0 = +inf", v64, 0x7FF0000000000000
        fnstsw ax
        and ax, 0x3f
        mov [v16], ax
        CHK16 "1/0 sets ZE", v16, 0x0004
        fninit
        fld qword [two]
        fsqrt
        fstp qword [v64]
        CHK64 "sqrt 2", v64, 0x3FF6A09E667F3BCD
        fld1
        fldz
        fcompp
        fnstsw ax
        and ax, 0x4700
        mov [v16], ax
        CHK16 "fcompp 0<1", v16, 0x0100
        fld1
        fld1
        fcompp
        fnstsw ax
        and ax, 0x4700
        mov [v16], ax
        CHK16 "fcompp 1=1", v16, 0x4000
        fldcw [cw_chop]
        fld qword [d2_7]
        fistp dword [v32]
        CHK32 "chop 2.7", v32, 2
        fldcw [cw_near]
        fld qword [d2_5]
        fistp dword [v32]
        CHK32 "near 2.5", v32, 2
        fld qword [d3_5]
        fistp dword [v32]
        CHK32 "near 3.5", v32, 4
        fldcw [cw_down]
        fld qword [dm2_5]
        fistp dword [v32]
        CHK32 "down -2.5", v32, -3
        fldcw [cw_up]
        fld qword [d2_5]
        frndint
        fistp dword [v32]
        CHK32 "up frndint 2.5", v32, 3
        fldcw [cw_near]
        fild word [w1234]
        fbstp [v80]
        CHK64 "fbstp 1234", v80, 0x1234
        fbld [v80]
        fistp word [v16]
        CHK16 "fbld 1234", v16, 1234
        fninit
        fldpi
        fld1
        fsave [savebuf]
        fninit
        frstor [savebuf]
        fstp qword [v64]
        CHK64 "frstor st0", v64, 0x3FF0000000000000
        fstp qword [v64]
        CHK64 "frstor st1", v64, 0x400921FB54442D18
        fninit
        fxam
        fnstsw ax
        and ax, 0x4500
        mov [v16], ax
        CHK16 "fxam empty", v16, 0x4100
        fld tword [odd80]
        fstp tword [v80]
        CHK64 "m80 trip lo", v80, 0x8000000000000001
        CHK16 "m80 trip hi", v80+8, 0x4003
        fld1
        fld1
        fpatan
        fstp qword [v64]
        CHK64 "fpatan", v64, 0x3FE921FB54442D18
        fild word [w3]
        fild word [w7]
        fprem
        fnstsw ax
        and ax, 0x4700
        mov [v16], ax
        fistp word [v16b]
        fstp st0
        CHK16 "fprem 7%3", v16b, 1
        CHK16 "fprem cc", v16, 0x4000
        fild word [w8]
        fxtract
        fistp word [v16]
        fistp word [v16b]
        CHK16 "fxtract sig", v16, 1
        CHK16 "fxtract exp", v16b, 3
        fild word [wm5]
        fchs
        fistp word [v16]
        CHK16 "fchs", v16, 5
        fninit
        fld qword [d1e30]
        fistp dword [v32]
        CHK32 "fistp overflow", v32, 0x80000000
        fnstsw ax
        and ax, 1
        mov [v16], ax
        CHK16 "overflow IE", v16, 1
        fld dword [f1_5]
        fstp dword [v32]
        CHK32 "m32 trip", v32, 0x3FC00000
        fninit
        fld1
        fld1
        fld1
        fld1
        fld1
        fld1
        fld1
        fld1
        fld1
        fnstsw ax
        and ax, 0x0241
        mov [v16], ax
        CHK16 "stack overflow", v16, 0x0241
        fninit
        ret

; ---------------- MMX ----------------
mmx_tests:
        fninit
        movq mm0, [qa]
        paddb mm0, [qff]
        movq [v64], mm0
        CHK64 "paddb", v64, 0x0001020304050607
        movq mm1, [qf0]
        paddusb mm1, [q20]
        movq [v64], mm1
        CHK64 "paddusb", v64, 0xFFFFFFFFFFFFFFFF
        movq mm1, [q10]
        psubusb mm1, [q20]
        movq [v64], mm1
        CHK64 "psubusb", v64, 0
        movq mm2, [qsw]
        paddsw mm2, [qsw1]
        movq [v64], mm2
        CHK64 "paddsw", v64, 0x7FFF80007FFF8000
        movq mm3, [qm1]
        pmullw mm3, [qm2]
        movq [v64], mm3
        CHK64 "pmullw", v64, 0x0000FFFE0000000C
        movq mm3, [qh1]
        pmulhw mm3, [qh2]
        movq [v64], mm3
        CHK64 "pmulhw", v64, 0x00000000FFFF1000
        movq mm4, [q1234]
        pmaddwd mm4, [q5678]
        movq [v64], mm4
        CHK64 "pmaddwd", v64, 0x0000003500000011
        movq mm5, [qa]
        punpcklbw mm5, [qb]
        movq [v64], mm5
        CHK64 "punpcklbw", v64, 0x0D050E060F070008
        movq mm5, [qpk]
        packsswb mm5, [q1234]
        movq [v64], mm5
        CHK64 "packsswb", v64, 0x040302017F10807F
        movq mm6, [qx]
        psllq mm6, 4
        movq [v64], mm6
        CHK64 "psllq 4", v64, 0x123456789ABCDEF0
        movq mm6, [qsw]
        psraw mm6, 1
        movq [v64], mm6
        CHK64 "psraw 1", v64, 0x3FFFC0003FFFC000
        movq mm6, [qx]
        movd mm7, [c64]
        psrlq mm6, mm7
        movq [v64], mm6
        CHK64 "psrlq 64", v64, 0
        movq mm0, [qa]
        pcmpeqb mm0, [qa2]
        movq [v64], mm0
        CHK64 "pcmpeqb", v64, 0xFF00FF00FF00FF00
        movq mm0, [qsw]
        pcmpgtw mm0, [q0]
        movq [v64], mm0
        CHK64 "pcmpgtw", v64, 0xFFFF0000FFFF0000
        mov eax, 0xdeadbeef
        movd mm1, eax
        movd ebx, mm1
        mov [v32], ebx
        CHK32 "movd", v32, 0xdeadbeef
        movq [v64], mm1
        CHK64 "movd zero-extends", v64, 0x00000000deadbeef
        movq mm0, [qx]
        fnsave [savebuf]
        CHK16 "mmx tags valid", savebuf+4, 0x0000
        CHK64 "mm0 = R0 significand", savebuf+14, 0x0123456789ABCDEF
        CHK16 "mm0 exponent", savebuf+22, 0xFFFF
        frstor [savebuf]
        movq [v64], mm0
        CHK64 "frstor keeps mm0", v64, 0x0123456789ABCDEF
        emms
        fnstenv [savebuf]
        CHK16 "emms tags empty", savebuf+4, 0xFFFF
        fninit
        ret

; ---------------- data ----------------
s_fpu:   db "fpu present", 10, 0
s_nofpu: db "no fpu", 10, 0
s_mmx:   db "mmx present", 10, 0
s_nommx: db "no mmx", 10, 0
s_done:  db "DONE", 10, 0
s_ok:    db "ok ", 0
s_fail:  db "FAIL ", 0
s_nl:    db 10, 0
        align 8
big:    dq 0x123456789ABCDEF1
two:    dq 2.0
d2_7:   dq 2.7
d2_5:   dq 2.5
d3_5:   dq 3.5
dm2_5:  dq -2.5
d1e30:  dq 1.0e30
f1_5:   dd 1.5
cw_chop: dw 0x0f7f
cw_near: dw 0x037f
cw_down: dw 0x077f
cw_up:   dw 0x0b7f
w1234:  dw 1234
w3:     dw 3
w7:     dw 7
w8:     dw 8
wm5:    dw -5
        align 8
odd80:  dq 0x8000000000000001
        dw 0x4003
        align 8
qa:     dq 0x0102030405060708
qa2:    dq 0x0103030505070709
qb:     dq 0x111213140D0E0F00
qff:    dq 0xFFFFFFFFFFFFFFFF
qf0:    dq 0xF0F0F0F0F0F0F0F0
q20:    dq 0x2020202020202020
q10:    dq 0x1010101010101010
qsw:    dq 0x7FFF80007FFF8000
qsw1:   dq 0x0001FFFF0001FFFF
qm1:    dq 0x0000FFFF00000003
qm2:    dq 0x0000000200000004
qh1:    dq 0x00000000FFFF4000
qh2:    dq 0x0000000000024000
q1234:  dq 0x0004000300020001
q5678:  dq 0x0008000700060005
qpk:    dq 0x007F0010FF000100
qx:     dq 0x0123456789ABCDEF
q0:     dq 0
c64:    dd 64
        align 8
tmpw:   dw 0
v16:    dw 0
v16b:   dw 0
v32:    dd 0
        align 8
v64:    dq 0
v80:    times 10 db 0
        align 16
savebuf: times 128 db 0
