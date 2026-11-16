; V13.COM: x86 speed workload (BrokenNes tools/x86-speed). Mode 13h, a per-pixel 16-bit real-mode loop over A0000 for FRAMES frames
; (no vsync wait), then text mode and "VDONE". The DOS-game shape: planar-free chunky VGA writes from real mode.
FRAMES equ 600
        org 100h
        mov ax, 13h
        int 10h
        mov ax, 0A000h
        mov es, ax
        mov bp, FRAMES
frame:  xor di, di
        xor dx, dx
row:    xor cx, cx
        mov bx, dx
        add bx, bp
px:     mov ax, cx
        add ax, bx
        xor al, dl
        stosb
        inc cx
        cmp cx, 320
        jb px
        inc dx
        cmp dx, 200
        jb row
        dec bp
        jnz frame
        mov ax, 3
        int 10h
        mov dx, done
        mov ah, 9
        int 21h
        mov ax, 4C00h
        int 21h
done    db "VDONE", 13, 10, "$"
