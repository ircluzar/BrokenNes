# VRUN room-graph explorer + corruption watch, driven entirely over the BrokenNes HTTP API.
#
# Uses the game's own RAM as the oracle rather than guessing from pixels. Addresses were validated
# live against the ROM on the user's Desktop (see notes below) - the published RAM map in
# docs/23-ram-map.md is measured at commit 2495a13, but THAT build shipped 2 bytes of new fields
# (brick_flash_timer/brick_flash_cxy at $0384/$0385) which shifted every later symbol +2. The
# Desktop ROM predates them, so symbols after the insertion point sit 2 bytes LOWER here. Verified
# individually: app_state reads 0 on the title screen and 1 in gameplay at $03AD (not $03AF), score
# poked to 9999 at $0393 showed up as "$09999" in the HUD, paused/player_x/player_y (all declared
# BEFORE the insertion point) match the doc exactly.
#
# If this script ever starts reading nonsense, re-validate before trusting it - that exact stale
# address trap is called out in tools/corruption_detector.lua's own header as having cost the VRUN
# project a round of false debugging.

. "$PSScriptRoot\vrun-play.ps1"

# --- validated addresses for the Desktop build --------------------------------
$script:A = @{
    app_state   = 941   # $03AD  0=Title 1=Game 2=Death 3=Shop 4=Ending 5=Options
    paused      = 876   # $036C
    current_room= 856   # $0358
    player_x    = 807   # $0327 (whole-pixel plane)
    player_y    = 808   # $0328
    player_hp   = 811   # $032B
    player_hp_max=810   # $032A
    score_lo    = 915   # $0393 (UU, little-endian)
    score_hi    = 916   # $0394
    score_dirty = 938   # $03CA
    nbr_id      = 803   # $0323..$0326  left/right/up/down room id (255 = none)
    nbr_flags   = 880   # $0370..$0373  ROOM_FLAG_SHOP = bit 0
    shop_cursor = 919   # $0397
    burst_level = 815   # $032F   } these live before the insertion point,
    range_level = 816   # $0330   } so their doc addresses are unchanged
    intensity   = 817   # $0331
    speed_level = 818   # $0332
    hover_level = 819   # $0333
    trail_level = 820   # $0334
    luck_level  = 920   # $0398 (doc $039A - 2)
}
$script:DIRS = @('Left','Right','Up','Down')   # order matches nbr_id / nbr_flags

function Get-Ram { param($S,[int]$Addr)
    (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path "/api/memory/peek?domain=System%20RAM&address=$Addr").value
}
function Set-Ram { param($S,[int]$Addr,[int]$Value)
    Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method POST -Path '/api/memory/poke' -Body @{ Domain='System RAM'; Address=$Addr; Value=$Value } | Out-Null
}
function Get-Score { param($S) (Get-Ram $S $A.score_hi) * 256 + (Get-Ram $S $A.score_lo) }

function Get-VrunState { param($S)
    $st = [ordered]@{
        AppState = Get-Ram $S $A.app_state
        Room     = Get-Ram $S $A.current_room
        X        = Get-Ram $S $A.player_x
        Y        = Get-Ram $S $A.player_y
        Hp       = Get-Ram $S $A.player_hp
        Score    = Get-Score $S
        Nbr      = @{}
    }
    for ($i = 0; $i -lt 4; $i++) {
        $st.Nbr[$DIRS[$i]] = [pscustomobject]@{
            Room  = Get-Ram $S ($A.nbr_id + $i)
            Flags = Get-Ram $S ($A.nbr_flags + $i)
        }
    }
    [pscustomobject]$st
}

# Which directions lead to a shop room, per the game's own neighbour-flag cache.
function Get-ShopDirs { param($S)
    $out = @()
    for ($i = 0; $i -lt 4; $i++) {
        if (((Get-Ram $S ($A.nbr_flags + $i)) -band 1) -ne 0 -and (Get-Ram $S ($A.nbr_id + $i)) -ne 255) { $out += $DIRS[$i] }
    }
    $out
}

# Hold a direction (jumping periodically, since doors can sit above ledges) until current_room
# changes, app_state leaves GAME (a shop opening counts!), or we give up.
function Walk-Until-Transition {
    param($S, [ValidateSet('Left','Right','Up','Down')]$Dir, [int]$MaxSeconds = 14)
    $room0 = Get-Ram $S $A.current_room
    $state0 = Get-Ram $S $A.app_state
    $deadline = (Get-Date).AddSeconds($MaxSeconds)
    $tick = 0
    while ((Get-Date) -lt $deadline) {
        $btns = @($Dir)
        if ($tick % 3 -eq 2) { $btns += 'A' }        # hop over ledges / reach raised doors
        Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method POST -Path '/api/input/set-buttons' `
            -Body @{ player = 1; buttons = $btns; holdMs = 420 } | Out-Null
        Start-Sleep -Milliseconds 520
        $r = Get-Ram $S $A.current_room
        $a = Get-Ram $S $A.app_state
        if ($a -ne $state0) { return [pscustomobject]@{ Changed=$true; Reason='app_state'; Room=$r; AppState=$a } }
        if ($r -ne $room0)  { return [pscustomobject]@{ Changed=$true; Reason='room';      Room=$r; AppState=$a } }
        $tick++
    }
    [pscustomobject]@{ Changed=$false; Reason='timeout'; Room=(Get-Ram $S $A.current_room); AppState=(Get-Ram $S $A.app_state) }
}

# --- corruption watch ---------------------------------------------------------
# Port of check 2 ("CHR scatter") from the project's own tools/corruption_detector.lua: the game
# only ever rewrites CHR for a small set of deliberately animated metatiles, so any OTHER tile
# changing means a $2007 write scattered into CHR-RAM - the mechanism behind the "stray pixels in
# black tiles" class of bug. We cannot allowlist by metatile id without the .mlb, so this reports
# the churn rate and the specific tiles instead, and it is the DELTA that matters: a stable set of
# a few animated tiles is healthy, a growing scatter across many tiles is not.
function New-ChrBaseline { param($S)
    (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/memory/peek-range?domain=CHR&address=0&length=4096').data
}
function Compare-Chr { param($S, $Baseline)
    $now = New-ChrBaseline $S
    $changed = @{}
    for ($i = 0; $i -lt [Math]::Min($Baseline.Count, $now.Count); $i++) {
        if ($Baseline[$i] -ne $now[$i]) { $changed[[int][Math]::Floor($i / 16)] = $true }
    }
    [pscustomobject]@{ TilesChanged = $changed.Keys.Count; Tiles = ($changed.Keys | Sort-Object); Now = $now }
}
