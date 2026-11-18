; INT 13h floppy read / write from V86 mode through the 82077AA (BrokenNes x86 BIOS, F000:9800). Entered from the IDE PIO code's dispatch
; (F000:E88B jumps here) with AL = task mode, DS = F000, SI = F000:DF01, the caller's registers pushed (push ds, es / pusha).
; Task (DF01..): drive, cyl, head, sector, eot, cmd, dma mode, addr lo, addr hi, page, cnt-1 lo, cnt-1 hi, rate, DOR
bits 16
org 0x9800
disp:   test al, al
        jz near 0xe8de              ; mode 0: nothing to do (popa / pop es, ds / iret)
        cmp al, 4
        je floppy
        jmp near 0xe88f             ; the IDE modes
floppy: mov bx, si
        mov al, 6                   ; DMA channel 2: mask, clear flip-flop, mode, address, page, count, unmask
        out 0x0a, al
        out 0x0c, al
        mov al, [bx+6]
        out 0x0b, al
        mov al, [bx+7]
        out 0x04, al
        mov al, [bx+8]
        out 0x04, al
        mov al, [bx+9]
        out 0x81, al
        mov al, [bx+10]
        out 0x05, al
        mov al, [bx+11]
        out 0x05, al
        mov al, 2
        out 0x0a, al
        mov dx, 0x3f2               ; motor on, DMA / IRQ on
        mov al, [bx+13]
        out dx, al
        mov dx, 0x3f7               ; data rate
        mov al, [bx+12]
        out dx, al
        ; SEEK drive/head, cylinder; wait for the drive's seek bit, SENSE INTERRUPT
        mov al, 0x0f
        call send
        call hddrv
        call send
        mov al, [bx+1]
        call send
        mov dx, 0x3f4
        mov ecx, 0x400000
.sk:    in al, dx
        test al, 0x0f
        jz .skd
        dec ecx
        jnz .sk
        jmp fail
.skd:   mov al, 0x08
        call send
        call recv
        call recv
        ; READ / WRITE DATA: cmd, head/drive, C, H, R, N=2, EOT, GPL 1Bh, DTL FFh
        mov al, [bx+5]
        call send
        call hddrv
        call send
        mov al, [bx+1]
        call send
        mov al, [bx+2]
        call send
        mov al, [bx+3]
        call send
        mov al, 2
        call send
        mov al, [bx+4]
        call send
        mov al, 0x1b
        call send
        mov al, 0xff
        call send
        mov cx, 7                   ; the result phase: ST0 ST1 ST2 C H R N
        lea di, [bx+15]
.rs:    call recv
        mov [di], al                ; (ROM: a write to F000 is ignored; the C# side has nothing to read back)
        inc di
        loop .rs
done:   jmp near 0xe8de
fail:   jmp near 0xe8de

hddrv:  mov al, [bx+2]              ; (head << 2) | drive
        shl al, 2
        or al, [bx]
        ret

send:   push ax                     ; wait RQM with DIO = 0, write the FIFO
        push ecx
        mov dx, 0x3f4
        mov ecx, 0x400000
.w:     in al, dx
        and al, 0xc0
        cmp al, 0x80
        je .go
        dec ecx
        jnz .w
.go:    pop ecx
        pop ax
        mov dx, 0x3f5
        out dx, al
        ret

recv:   push ecx                    ; wait RQM with DIO = 1, read the FIFO
        mov dx, 0x3f4
        mov ecx, 0x400000
.w:     in al, dx
        and al, 0xc0
        cmp al, 0xc0
        je .go
        dec ecx
        jnz .w
.go:    pop ecx
        mov dx, 0x3f5
        in al, dx
        ret
