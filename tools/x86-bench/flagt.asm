; FLAGT.COM - x86 ALU/flag differential test. For every op: hash of results + defined flags
; over all operand pairs from VALS and two input-flag states. Prints "name hash".
cpu 686
org 100h
section .text
start:
    mov ax, 3500h
    int 21h
    mov [old0], bx
    mov [old0+2], es
    mov dx, de_h
    mov ax, 2500h
    int 21h
    mov si, tab
next:
    lodsw
    or ax, ax
    jz done
    mov [rout], ax
    lodsw
    mov [mask], ax
    mov [nameptr], si
    mov byte [verb], 0
    cmp byte [80h], 1
    jbe pn
    push si
    mov di, 82h
cmpn: mov al, [si]
    mov ah, [di]
    cmp ah, 13
    je cmpe
    cmp ah, ' '
    je cmpe
    cmp al, ah
    jne cmpx
    inc si
    inc di
    jmp cmpn
cmpe: cmp al, 0
    jne cmpx
    mov byte [verb], 1
cmpx: pop si
    ; print name
pn: lodsb
    or al, al
    jz pe
    mov dl, al
    mov ah, 2
    int 21h
    jmp pn
pe: push si
    mov dword [hash], 0
    xor di, di            ; i*4
li: xor bp, bp            ; j*4
lj: mov word [fsel], 0
lk: mov eax, [vals+bp]
    ; skip-zero divisor ops (mask bit 14)
nz: mov eax, [fins]
    cmp word [fsel], 0
    je f0
    mov eax, 8d5h
f0: mov [fin], eax
    mov eax, [vals+di]
    mov ebx, [vals+bp]
    mov ecx, ebx
    mov edx, eax
    not edx
    call [rout]
    push eax
    push ebx
    push ecx
    push edx
    push esi
    cmp byte [verb], 0
    je nv
    mov dx, crlf
    mov ah, 9
    int 21h
    mov eax, [vals+di]
    call phsp
    mov eax, [vals+bp]
    call phsp
    mov eax, [fin]
    call phsp
    mov eax, [esp+16]
    call phsp
    mov eax, [esp+12]
    call phsp
    mov eax, [esp+4]
    call phsp
    mov eax, [esp]
    call phsp
nv: pop esi
    pop edx
    pop ecx
    pop ebx
    pop eax
    ; hash eax, ebx, ecx, edx, flags&mask
    call mix
    mov eax, ebx
    call mix
    mov eax, ecx
    call mix
    mov eax, edx
    call mix
    movzx eax, word [mask]
    and ax, 0fffh
    test word [mask], 8000h   ; shift op: OF only defined for count 1
    jz ns
    mov bx, [vals+bp]
    and bx, 1fh
    cmp bx, 1
    je ns
    and ax, ~800h
ns: and eax, esi
    call mix
skip:
    inc word [fsel]
    cmp word [fsel], 2
    jb lk
    add bp, 4
    cmp bp, NV*4
    jb lj
    add di, 4
    cmp di, NV*4
    jb li
    mov dl, ' '
    mov ah, 2
    int 21h
    mov eax, [hash]
    call phex
    mov dx, crlf
    mov ah, 9
    int 21h
    pop si
    jmp next
done:
    push ds
    lds dx, [old0]
    mov ax, 2500h
    int 21h
    pop ds
    mov ax, 4c00h
    int 21h

de_h: add sp, 6          ; drop the #DE frame, return from the test routine
    mov esi, 0DEh
    ret

mix: ; hash = rol(hash,5) ^ eax, + golden
    push eax
    mov eax, [hash]
    rol eax, 5
    xor eax, [esp]
    add eax, 9e3779b9h
    mov [hash], eax
    pop eax
    ret

phsp: call phex
    mov dl, ' '
    mov ah, 2
    int 21h
    ret
phex: push cx
    mov cx, 8
ph1: rol eax, 4
    push eax
    and al, 0fh
    add al, '0'
    cmp al, '9'
    jbe ph2
    add al, 7
ph2: mov dl, al
    mov ah, 2
    int 21h
    pop eax
    loop ph1
    pop cx
    ret

%define ARITH 8d5h
%define LOGIC 8c5h   ; AF undefined
%macro R 3+ ; name, mask, instruction
section .data
    dw %%c, %2
    %defstr %%nm %1
    db %%nm, 0
section .text
%%c: push dword [fin]
    popfd
    %3
    pushfd
    pop esi
    ret
%endmacro
%macro R2 4 ; name, mask, insn1, insn2
section .data
    dw %%c, %2
    %defstr %%nm %1
    db %%nm, 0
section .text
%%c: push dword [fin]
    popfd
    %3
    %4
    pushfd
    pop esi
    ret
%endmacro
%macro R3 3 ; op, mask, namebase : 8/16/32 bit reg forms
    R %{3}8, %2, %1 al, bl
    R %{3}16, %2, %1 ax, bx
    R %{3}32, %2, %1 eax, ebx
%endmacro
%macro S3 2 ; shift op by cl
    R %{2}8, 8000h|LOGIC, %1 al, cl
    R %{2}16, 8000h|LOGIC, %1 ax, cl
    R %{2}32, 8000h|LOGIC, %1 eax, cl
    R %{2}8i1, LOGIC, %1 al, 1
    R %{2}32i1, LOGIC, %1 eax, 1
%endmacro
%macro U3 3 ; unary op
    R %{3}8, %2, %1 al
    R %{3}16, %2, %1 ax
    R %{3}32, %2, %1 eax
%endmacro

section .data
tab:
section .text
    R3 add, ARITH, add
    R3 adc, ARITH, adc
    R3 sub, ARITH, sub
    R3 sbb, ARITH, sbb
    R3 cmp, ARITH, cmp
    R3 and, LOGIC, and
    R3 or, LOGIC, or
    R3 xor, LOGIC, xor
    R3 test, LOGIC, test
    R3 xadd, ARITH, xadd
    R3 cmpxchg, ARITH, cmpxchg
    U3 inc, ARITH, inc
    U3 dec, ARITH, dec
    U3 neg, ARITH, neg
    U3 not, ARITH, not
    S3 shl, shl
    S3 shr, shr
    S3 sar, sar
    S3 rol, rol
    S3 ror, ror
    S3 rcl, rcl
    S3 rcr, rcr
    R shld16, 8000h|ARITH, shld ax, bx, cl
    R shld32, 8000h|ARITH, shld eax, ebx, cl
    R shrd16, 8000h|ARITH, shrd ax, bx, cl
    R shrd32, 8000h|ARITH, shrd eax, ebx, cl
    R mul8, 801h, mul bl
    R mul16, 801h, mul bx
    R mul32, 801h, mul ebx
    R imul8, 801h, imul bl
    R imul16, 801h, imul bx
    R imul32, 801h, imul ebx
    R imul2r16, 801h, imul ax, bx
    R imul2r32, 801h, imul eax, ebx
    R imul3i, 801h, imul eax, ebx, -3
    R2 div8, 4000h, {movzx ax, al}, {div bl}
    R2 div16, 4000h, {xor dx, dx}, {div bx}
    R2 div32, 4000h, {xor edx, edx}, {div ebx}
    R2 idiv8, 4000h, {movsx ax, al}, {idiv bl}
    R2 idiv16, 4000h, {cwd}, {idiv bx}
    R bsf16, 040h, bsf ax, bx
    R bsf32, 040h, bsf eax, ebx
    R bsr16, 040h, bsr ax, bx
    R bsr32, 040h, bsr eax, ebx
    R bt32, 001h, bt eax, ebx
    R bts32, 001h, bts eax, ebx
    R btr16, 001h, btr ax, bx
    R btc32, 001h, btc eax, ecx
    R daa, 0d5h, daa
    R das, 0d5h, das
    R aaa, 011h, aaa
    R aas, 011h, aas
    R aam, 0c4h|4000h, aam 10
    R aad, 0c4h, aad 10
    R aad7, 0c4h, aad 7
    R cbw, 0, cbw
    R cwde, 0, cwde
    R cwd, 0, cwd
    R cdq, 0, cdq
    R bswap, 0, bswap eax
    R setcc, 0, setnbe al
    R setl, 0, setl bl
    R movsx, 0, movsx eax, bl
    R movzx, 0, movzx eax, bx
    R salc, 0, db 0d6h
    R lahf, 0, lahf
    R sahf, ARITH, sahf
    R cmc, ARITH, cmc
    R xchg, 0, xchg ebx, edx
    R lea, 0, lea eax, [ebx+eax*4+12h]
    R btm16, 001h, bt word [btbuf], bx
    R btsm16, 001h, bts word [btbuf], ax
    R btcm16, 001h, btc word [btbuf], bx
    R btm16i, 001h, bt word [btbuf], 33
    R btsm16i, 001h, bts word [btbuf], 47
    R btm32i, 001h, bt dword [btbuf], 61
    R2 btrd, 001h, {movsx ebx, bx}, {btr dword [btbuf], ebx}
    R2 btchk, 0, {mov ax, [btbuf-1000h]}, {mov bx, [btbuf+0FFEh]}

section .data
    dw 0
old0: dd 0
fins: dd 2
nameptr: dw 0
verb: db 0
fin: dd 0
hash: dd 0
rout: dw 0
mask: dw 0
fsel: dw 0
crlf: db 13,10,'$'
vals: dd 0,1,2,7Fh,80h,0FFh,7FFFh,8000h,0FFFFh,7FFFFFFFh,80000000h,0FFFFFFFFh,12345678h,0EDCBA987h,1Fh,20h,21h,9,10h,99h,8Fh
NV equ ($-vals)/4
    times 1100h db 0
btbuf: times 1100h db 0
