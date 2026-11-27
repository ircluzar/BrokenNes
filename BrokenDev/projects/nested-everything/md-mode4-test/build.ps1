# Builds out\md_mode4_test.md with SGDK 2.11's m68k binutils (BrokenDev toolchain 'sgdk'; no SGDK library is used).
param([string]$Sgdk = $(if ($env:BROKENDEV_HOME) { Join-Path $env:BROKENDEV_HOME 'toolchains\sgdk\2.11\sgdk' } else { 'T:\BrokenDev\env\toolchains\sgdk\2.11\sgdk' }))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force out | Out-Null
python -I gen_data.py out\data.inc; if ($LASTEXITCODE) { throw 'gen_data failed' }
& "$Sgdk\bin\as.exe" -m68000 --register-prefix-optional -I out -o out\md_mode4_test.o md_mode4_test.s; if ($LASTEXITCODE) { throw 'as failed' }
& "$Sgdk\bin\ld.exe" -e 0 -Ttext=0 --oformat binary -o out\md_mode4_test.md out\md_mode4_test.o; if ($LASTEXITCODE) { throw 'ld failed' }
python -I gen_data.py --checksum out\md_mode4_test.md; if ($LASTEXITCODE) { throw 'checksum failed' }
(Get-FileHash out\md_mode4_test.md -Algorithm SHA256).Hash.ToLower() + '  md_mode4_test.md'
