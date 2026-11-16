; CPUDET.COM: which CPU is this? The classic DOS detection (our own code): 8086 / 286 / 386 by the FLAGS bits, 486 by EFLAGS.AC,
; CPUID by EFLAGS.ID (vendor, family / model / stepping, FPU / TSC / MMX bits), the FPU by FNINIT + FNSTSW, and the core clock by
; RDTSC across 18 BIOS timer ticks. Prints to the screen (DOS) and to port E9h. Used by Workshop --x86cpu --dos.
; Build: nasm -f bin cpudet.asm -o CPUDET.COM
        bits 16
        org 0x100
start:
        mov dx, s_title
        call print
        ; ---- 8086 / 286 / 386+ (the probes change FLAGS: keep the caller's and put them back) ----
        pushf
        pop word [flags0]
        pushf
        pop ax
        and ax, 0x0fff
        push ax
        popf
        pushf
        pop ax
        and ax, 0xf000
        cmp ax, 0xf000
        jne .not86
        mov dx, s_8086
        call print
        jmp fpu
.not86: mov ax, 0xf000
        push ax
        popf
        pushf
        pop ax
        and ax, 0xf000
        push word [flags0]
        popf
        test ax, ax
        jnz .is386
        mov dx, s_286
        call print
        jmp fpu
.is386: ; ---- AC (486) ----
        pushfd
        pop eax
        mov ecx, eax
        xor eax, 0x40000
        push eax
        popfd
        pushfd
        pop eax
        push ecx
        popfd
        xor eax, ecx
        test eax, 0x40000
        jnz .is486
        mov dx, s_386
        call print
        jmp fpu
.is486: ; ---- ID (CPUID) ----
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
        jnz .cpuid
        mov dx, s_486
        call print
        jmp fpu
.cpuid: xor eax, eax
        cpuid
        mov [vendor], ebx
        mov [vendor + 4], edx
        mov [vendor + 8], ecx
        mov dx, s_vend
        call print
        mov dx, vendor
        call print
        mov eax, 1
        cpuid
        mov [feat], edx
        mov [sig], eax
        mov dx, s_fam
        call print
        mov eax, [sig]
        shr eax, 8
        and ax, 15
        call pdec
        mov dx, s_mod
        call print
        mov eax, [sig]
        shr eax, 4
        and ax, 15
        call pdec
        mov dx, s_step
        call print
        mov eax, [sig]
        and ax, 15
        call pdec
        mov dx, s_nl
        call print
        cmp byte [sig + 1], 5
        jb .c486
        mov dx, s_586
        call print
        jmp .feat
.c486:  mov dx, s_486c
        call print
.feat:  test dword [feat], 0x800000
        jz .nommx
        mov dx, s_mmx
        call print
        jmp .tsc
.nommx: mov dx, s_nommx
        call print
.tsc:   test dword [feat], 0x10
        jz fpu
        call mhz
fpu:    mov word [tmp], 0x5a5a
        fninit
        fnstsw [tmp]
        cmp word [tmp], 0
        jne .nofpu
        fnstcw [tmp]
        and word [tmp], 0x103f
        cmp word [tmp], 0x003f
        jne .nofpu
        mov dx, s_fpu
        call print
        jmp end
.nofpu: mov dx, s_nofpu
        call print
end:    mov dx, s_end
        call print
        mov ax, 0x4c00
        int 0x21

; the core clock: RDTSC across 18 timer ticks (18 * 54925.4 us = 988658 us)
mhz:    push es
        xor ax, ax
        mov es, ax
        mov bx, [es:0x46c]
.w1:    cmp bx, [es:0x46c]
        je .w1
        mov bx, [es:0x46c]
        rdtsc
        mov [t0], eax
        mov [t0 + 4], edx
        add bx, 18
.w2:    cmp bx, [es:0x46c]
        jne .w2
        rdtsc
        sub eax, [t0]
        sbb edx, [t0 + 4]
        mov ecx, 988658
        div ecx
        pop es
        push ax
        mov dx, s_mhz
        call print
        pop ax
        call pdec
        mov dx, s_mhz2
        call print
        ret

; print a $-terminated string at dx (DOS + port E9h)
print:  push si
        push ax
        mov si, dx
.l:     lodsb
        cmp al, '$'
        je .d
        out 0xe9, al
        jmp .l
.d:     mov ah, 9
        int 0x21
        pop ax
        pop si
        ret

; print ax in decimal
pdec:   push bx
        push cx
        push dx
        mov bx, 10
        xor cx, cx
.a:     xor dx, dx
        div bx
        push dx
        inc cx
        test ax, ax
        jnz .a
.b:     pop dx
        add dl, 0x30
        mov [ch1], dl
        mov dx, ch1
        call print
        loop .b
        pop dx
        pop cx
        pop bx
        ret

s_title: db "CPUDET", 13, 10, "$"
s_8086:  db "CPU: 8086/8088", 13, 10, "$"
s_286:   db "CPU: 80286", 13, 10, "$"
s_386:   db "CPU: 80386 (no AC flag)", 13, 10, "$"
s_486:   db "CPU: 80486 (AC flag, no CPUID)", 13, 10, "$"
s_486c:  db "CPU: 80486 with CPUID", 13, 10, "$"
s_586:   db "CPU: Pentium class", 13, 10, "$"
s_vend:  db "CPUID vendor: $"
s_fam:   db 13, 10, "family $"
s_mod:   db " model $"
s_step:  db " stepping $"
s_mmx:   db "MMX: yes", 13, 10, "$"
s_nommx: db "MMX: no", 13, 10, "$"
s_mhz:   db "TSC clock: $"
s_mhz2:  db " MHz", 13, 10, "$"
s_fpu:   db "FPU: present", 13, 10, "$"
s_nofpu: db "FPU: none", 13, 10, "$"
s_nl:    db 13, 10, "$"
s_end:   db "END", 13, 10, "$"
ch1:     db 0, "$"
vendor:  times 12 db 0
         db "$"
tmp:     dw 0
flags0:  dw 0
         align 4
feat:    dd 0
sig:     dd 0
t0:      dd 0, 0
