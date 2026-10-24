# BrokenDev's WideMess devkit: 'brokendev new widemess <name>'. Requires Common and Manifest.
#
# A new project is the template (BrokenDev/widemess/template) copied into its own folder with its name filled in, and a manifest
# (BrokenDev/widemess/template.project.json filled in) in projects/<name>/, so build, verify, run, play and compat know it at once.
# The guide: docs/projects/widemess-devkit.md.

Set-StrictMode -Version Latest

function Find-BdPvsneslib {
    <# PVSnesLib's folder for WideMess builds: BROKENDEV_PVSNESLIB, else the copy in the Super VRUN workspace beside the repository (a git
       worktree under .claude\worktrees looks beside the main checkout). $null when there is none (devkitsnes\bin\816-tcc.exe is the test). #>
    $c = @()
    if ($env:BROKENDEV_PVSNESLIB) { $c += $env:BROKENDEV_PVSNESLIB }
    $repo = Get-BdRepoRoot
    $c += (Join-Path (Split-Path $repo -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib')
    if ($repo -match '^(.*?)\\[^\\]+\\\.claude\\worktrees\\') { $c += (Join-Path $Matches[1] 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib') }
    foreach ($p in $c) { if ($p -and (Test-Path -LiteralPath (Join-Path $p 'devkitsnes\bin\816-tcc.exe'))) { return (Resolve-Path -LiteralPath $p).Path } }
    $null
}

function New-BdWideMessProject {
    <#
      Creates a WideMess project. $Name: the project id (lower-case letters, digits, dashes); the ROM is called after it (dashes become _).
      $Path: its folder (default BROKENDEV_HOME\projects\<name>); it must not exist or be empty. $Title: the game's title (default the name).
      Returns { Id; Folder; Manifest }. Throws, having written nothing, when the name is taken or the folder is not empty.
    #>
    param([Parameter(Mandatory)][string]$Name, [string]$Path = '', [string]$Title = '')
    if ($Name -notmatch '^[a-z][a-z0-9\-]{0,30}$') { throw "the name '$Name' must be lower-case letters, digits and dashes, starting with a letter (it is the project id and names the ROM)" }
    $tpl = Join-Path (Get-BdRoot) 'widemess\template'
    $tplManifest = Join-Path (Get-BdRoot) 'widemess\template.project.json'
    if (-not (Test-Path -LiteralPath (Join-Path $tpl 'wms.json'))) { throw "the template is missing: $tpl" }
    $manifestDir = Join-Path (Get-BdProjectsDir) $Name
    if (Test-Path -LiteralPath (Join-Path $manifestDir 'project.json')) { throw "a project called '$Name' exists already ($manifestDir)" }
    $folderText = ''
    if (-not $Path) {
        $Path = Join-Path (Join-Path (Get-BdHome) 'projects') $Name
        $folderText = "{home}/projects/$Name"
    }
    $Path = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $folderText) {
        $repo = (Get-BdRepoRoot).TrimEnd('\') + '\'
        $folderText = if ($Path.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase)) { '{repo}/' + $Path.Substring($repo.Length).Replace('\', '/') } else { $Path.Replace('\', '/') }
    }
    if ((Test-Path -LiteralPath $Path) -and @(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) { throw "the folder $Path is not empty: pick another (-Path) or empty it" }
    $rom = $Name.Replace('-', '_')
    if (-not $Title) { $Title = ($Name.Replace('-', ' ')).ToUpperInvariant() }
    if ($Title -match '["\\{}]') { throw "the title must not contain quotes, backslashes or braces" }

    # the source: every template file, the placeholders filled in (text files only; the art and the audio are copied as they are)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($f in @(Get-ChildItem -LiteralPath $tpl -Recurse -File)) {
        if ($f.FullName -match '\\build(\\|$)') { continue }
        $rel = $f.FullName.Substring($tpl.Length).TrimStart('\')
        $dest = Join-Path $Path $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        if ($f.Extension -in '.c', '.h', '.asm', '.json', '.md', '.txt') {
            $t = [System.IO.File]::ReadAllText($f.FullName).Replace('{{NAME}}', $rom).Replace('{{TITLE}}', $Title)
            [System.IO.File]::WriteAllText($dest, $t, $utf8)
        } else { Copy-Item -LiteralPath $f.FullName -Destination $dest }
    }
    [System.IO.File]::WriteAllText((Join-Path $Path '.gitignore'), "build/`n", $utf8)

    # the manifest
    $m = [System.IO.File]::ReadAllText($tplManifest).Replace('{{ID}}', $Name).Replace('{{NAME}}', $rom).Replace('{{TITLE}}', $Title).Replace('{{FOLDER}}', $folderText)
    $obj = $m | ConvertFrom-Json
    $errs = @(Test-BdManifest -M $obj -ExpectedId $Name)
    if ($errs.Count -gt 0) { Remove-Item -LiteralPath $Path -Recurse -Force; throw ("the template's manifest does not validate (a devkit bug):`n  " + ($errs -join "`n  ")) }
    New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $manifestDir 'project.json'), $m, $utf8)
    [pscustomobject]@{ Id = $Name; Folder = $Path; FolderText = $folderText; Manifest = (Join-Path $manifestDir 'project.json'); Rom = "$rom.wms" }
}
