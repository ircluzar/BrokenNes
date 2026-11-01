# Hunts for a VRUN shop door and reports the room graph as it goes, watching for corruption.
#
# Traversal mechanic (read out of src/game/main.fab, not guessed):
#   * A room transition fires when the player crosses the SCREEN EDGE (player_x.a < 8 / > 239,
#     player_y.a < 8 / > 231) - not by standing on a door cell.
#   * Door cells start CM_DOOR|CM_SOLID (0x09) and physically block you. They are opened by
#     SHOOTING them: try_open_door_run() is only ever called from update_bullets()'s TIER_PLAYER
#     branch, and it flood-fills the whole door run, clearing CM_SOLID|CM_DOOR.
#   * If the neighbour room's flags have ROOM_FLAG_SHOP (bit 0), crossing that edge calls
#     open_shop_if_fresh() -> app_state = APP_SHOP (3) instead of entering the room.
#
# Harness shortcut, declared up front: rather than platform the player across each room to line up
# with its door, this pokes player_y to the door row and player_x next to it, then plays the door
# open with real button input. The door-open and the transition itself are therefore genuinely
# exercised by the emulator; only the walk across the room is skipped.

. "$PSScriptRoot\vrun-explore.ps1"

function Get-CollisionMap { param($S)
    (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/memory/peek-range?domain=System%20RAM&address=1280&length=224').data
}

# Door cells on a given edge, as pixel rows/columns.
function Get-EdgeDoors { param($Cm, [ValidateSet('Left','Right','Up','Down')]$Dir)
    $out = @()
    switch ($Dir) {
        'Left'  { for ($cy=0;$cy -lt 14;$cy++){ if ($Cm[$cy*16+0]  -band 8) { $out += ,@(0,$cy) } } }
        'Right' { for ($cy=0;$cy -lt 14;$cy++){ if ($Cm[$cy*16+15] -band 8) { $out += ,@(15,$cy) } } }
        'Up'    { for ($cx=0;$cx -lt 16;$cx++){ if ($Cm[0*16+$cx]  -band 8) { $out += ,@($cx,0) } } }
        'Down'  { for ($cx=0;$cx -lt 16;$cx++){ if ($Cm[13*16+$cx] -band 8) { $out += ,@($cx,13) } } }
    }
    $out
}

# Shoot the door on $Dir open, then cross the edge. Returns the new room / app_state.
function Cross-Door { param($S, [ValidateSet('Left','Right','Up','Down')]$Dir, [int]$MaxTries = 10)
    $cm = Get-CollisionMap $S
    $doors = Get-EdgeDoors $cm $Dir
    if ($doors.Count -eq 0) {
        # A door that has already been shot open has had CM_DOOR (and CM_SOLID) cleared, so it no
        # longer looks like a door at all - it is just a hole. Fall back to any passable edge cell.
        $doors = @()
        switch ($Dir) {
            'Left'  { for ($cy=0;$cy -lt 14;$cy++){ if (-not ($cm[$cy*16+0]  -band 1)) { $doors += ,@(0,$cy) } } }
            'Right' { for ($cy=0;$cy -lt 14;$cy++){ if (-not ($cm[$cy*16+15] -band 1)) { $doors += ,@(15,$cy) } } }
            'Up'    { for ($cx=0;$cx -lt 16;$cx++){ if (-not ($cm[0*16+$cx]  -band 1)) { $doors += ,@($cx,0) } } }
            'Down'  { for ($cx=0;$cx -lt 16;$cx++){ if (-not ($cm[13*16+$cx] -band 1)) { $doors += ,@($cx,13) } } }
        }
        if ($doors.Count -eq 0) { return [pscustomobject]@{ Ok=$false; Why='no door or opening on that edge' } }
    }
    $room0 = Get-Ram $S $A.current_room

    # aim at the middle door cell of the run
    $mid = $doors[[int][Math]::Floor($doors.Count/2)]
    $cx, $cy = $mid[0], $mid[1]
    switch ($Dir) {
        'Left'  { Set-Ram $S $A.player_y ($cy*16); Set-Ram $S $A.player_x 24  }
        'Right' { Set-Ram $S $A.player_y ($cy*16); Set-Ram $S $A.player_x 224 }
        'Up'    { Set-Ram $S $A.player_x ($cx*16); Set-Ram $S $A.player_y 24  }
        'Down'  { Set-Ram $S $A.player_x ($cx*16); Set-Ram $S $A.player_y 200 }
    }
    for ($i=0; $i -lt $MaxTries; $i++) {
        Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method POST -Path '/api/input/set-buttons' `
            -Body @{ player=1; buttons=@($Dir,'B'); holdMs=300 } | Out-Null
        Start-Sleep -Milliseconds 480
        $r = Get-Ram $S $A.current_room
        $a = Get-Ram $S $A.app_state
        if ($a -eq 3)      { return [pscustomobject]@{ Ok=$true; Shop=$true;  Room=$r; AppState=$a } }
        if ($r -ne $room0) { return [pscustomobject]@{ Ok=$true; Shop=$false; Room=$r; AppState=$a } }
        # re-seat vertically in case gravity dragged us off the door row
        if ($Dir -eq 'Left' -or $Dir -eq 'Right') { Set-Ram $S $A.player_y ($cy*16) }
    }
    [pscustomobject]@{ Ok=$false; Why='did not cross'; Room=(Get-Ram $S $A.current_room); AppState=(Get-Ram $S $A.app_state) }
}
