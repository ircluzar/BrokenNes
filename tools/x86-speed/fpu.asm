; FPU.COM: x86 speed workload (BrokenNes tools/x86-speed). A Quake-shaped x87 loop from real mode: a 3x3 matrix times a vector (FLD / FMUL /
; FADDP from memory), a perspective divide (FDIV), FSQRT, FIST / FILD round trips and FCOM + FNSTSW branches, ITER times, then "FDONE".
ITER equ 400000
        org 100h
        finit
        mov cx, ITER & 0ffffh
        mov si, ITER >> 16
        inc si
top:    fld dword [vx]            ; x' = m0*x + m1*y + m2*z
        fmul dword [m]
        fld dword [vy]
        fmul dword [m+4]
        faddp st1, st0
        fld dword [vz]
        fmul dword [m+8]
        faddp st1, st0
        fld dword [vx]            ; z' = m6*x + m7*y + m8*z
        fmul dword [m+24]
        fld dword [vy]
        fmul dword [m+28]
        faddp st1, st0
        fld dword [vz]
        fmul dword [m+32]
        faddp st1, st0
        fadd dword [zofs]
        fdivp st1, st0            ; x' / z'
        fst dword [sx]
        fmul st0, st0
        fsqrt
        fistp word [ix]
        fild word [ix]
        fcomp dword [lim]
        fnstsw ax
        sahf
        jb small
        fld dword [vx]            ; drift the vector so the values move
        fmul dword [decay]
        fstp dword [vx]
small:  fld dword [vy]
        fadd dword [step]
        fstp dword [vy]
        loop top
        dec si
        jnz top
        mov dx, done
        mov ah, 9
        int 21h
        mov ax, 4C00h
        int 21h
        align 4096
m       dd 0.8, -0.6, 0.1,  0.6, 0.8, 0.2,  0.05, 0.1, 0.99
vx      dd 100.0
vy      dd 50.0
vz      dd 300.0
zofs    dd 400.0
lim     dd 1000.0
decay   dd 0.999
step    dd 0.001
sx      dd 0
ix      dw 0
        align 4096
done    db "FDONE", 13, 10, "$"
