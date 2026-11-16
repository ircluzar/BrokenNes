; VLFB.COM: x86 speed workload (BrokenNes tools/x86-speed). VBE mode 114h (800x600x16) with the linear framebuffer, written from unreal
; mode by a per-pixel loop for FRAMES frames (no vsync wait: pure throughput), then text mode and "VDONE".
FRAMES equ 240
        org 100h
        mov ax, 4F01h
        mov cx, 114h
        mov di, mib
        int 10h
        cmp ax, 4Fh
        jne fail
        mov ax, 4F02h
        mov bx, 4114h
        int 10h
        cmp ax, 4Fh
        jne fail
        ; unreal mode: ES with a 4 GB limit
        xor eax, eax
        mov ax, cs
        shl eax, 4
        add eax, gdt
        mov [gdtr + 2], eax
        cli
        lgdt [gdtr]
        mov eax, cr0
        or al, 1
        mov cr0, eax
        jmp short $+2
        mov bx, 8
        mov es, bx
        and al, 0FEh
        mov cr0, eax
        jmp short $+2
        sti
        mov ebp, FRAMES
frame:  mov edi, [mib + 28h]
        xor edx, edx                 ; y
row:    xor ecx, ecx                 ; x
        mov ebx, edx
        add ebx, ebp
px:     lea eax, [ecx + ebx]
        xor eax, edx
        mov [es:edi], ax
        add edi, 2
        inc ecx
        cmp ecx, 800
        jb px
        inc edx
        cmp edx, 600
        jb row
        dec ebp
        jnz frame
        mov ax, 3
        int 10h
        mov dx, done
        mov ah, 9
        int 21h
        mov ax, 4C00h
        int 21h
fail:   mov ax, 3
        int 10h
        mov dx, nov
        mov ah, 9
        int 21h
        mov ax, 4C01h
        int 21h
done    db "VDONE", 13, 10, "$"
nov     db "NO VBE", 13, 10, "$"
gdtr    dw 15
        dd 0
        align 8
gdt     dq 0
        dq 00CF92000000FFFFh
mib     times 256 db 0
