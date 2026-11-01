# PowerShell client for the BrokenNes desktop app's local HTTP control API.
#
# ============================================================================================
# READ THIS FIRST: the HTTP API is the PREFERRED way to drive BrokenNes.Windows.exe for tests.
# ============================================================================================
#
# BrokenNes.Windows hosts an ASP.NET Core minimal-API server on loopback (Windows/webapi/) with
# 148 endpoints across 19 route-group files. Almost everything a UAT script needs - loading a ROM,
# pausing/resuming, switching CPU/PPU/APU cores and shaders, peeking/poking memory, reading CPU
# registers and the PPU framebuffer, firing RTC blasts, quick save/load states, switching the
# shell between the web front end and the native emulator view - is reachable over HTTP with no
# window focus, no z-order games, and no UI Automation at all.
#
# `UiaHelpers.ps1` (UI Automation) is the FALLBACK, for the handful of things the API genuinely
# cannot reach: the native WinForms MenuStrip's own item states (is "CPU > FIX" actually listed
# and checked?), the WebView2 front end's rendered DOM, message boxes, and screenshots. Reach for
# UIA only after checking that no endpoint covers what you need.
#
# *** WARNING - NEVER kill BrokenNes by PROCESS NAME. This machine runs several at once. ***
# The single most expensive false bug this harness has produced was "rapid keyboard input silently
# kills BrokenNes.Windows.exe" (UAT/findings/direct-play-r2.md, reproduced 3/3, chased across
# multiple sessions). It was not a product bug at all. It was this line, in some agent's
# clean-slate preamble:
#
#     Get-Process -Name BrokenNes.Windows -ErrorAction SilentlyContinue | Stop-Process -Force
#
# On a machine where several agents each run their own instance, that kills EVERY agent's
# emulator, not just leftovers from your own run. The victim sees a perfect silent death: no
# dialog, no WerFault, no event-log entry, and ExitCode -1 (which is simply what TerminateProcess
# reports). It looks exactly like a mysterious in-process crash and it is not one.
# This was proved on 2026-09-04 by watching two live instances - one under test, one belonging to
# another agent - vanish in the SAME 474ms sampling window, and by confirming the app's own
# instrumentation records nothing at all on that path (see Windows/Diagnostics/ShutdownDiagnostics.cs;
# a graceful close logs WM_CLOSE -> OnFormClosing -> ApplicationExit -> ProcessExit and exits 0).
#
# So: kill by PID, never by name.
#     Stop-BrokenNesInstance -ProcessId $inst.ProcessId -Force      # correct - yours only
# and before assuming exclusivity, look first:
#     Assert-BrokenNesNoForeignInstances -MineProcessId $inst.ProcessId
#
# If a test dies unexplained, check for a foreign instance BEFORE filing a product bug. Set
# BROKENNES_DIAG=1 on the app under test and read its shutdown log
# (%LOCALAPPDATA%\BrokenNes\diagnostics\shutdown-<pid>-*.log): a log that simply stops, with no
# lifecycle lines, means the process was terminated from outside.
#
# *** WARNING - the modal-dialog Invoke() hang. Do NOT re-walk into this. ***
# Driving the native menu with UI Automation's InvokePattern.Invoke() on an item that opens a
# MODAL dialog (most notoriously "Emulator > Load Rom...", which opens an OpenFileDialog) BLOCKS
# THE CALLING POWERSHELL THREAD until that dialog is dismissed. The dialog runs its own message
# loop on the app's UI thread and Invoke() does not return until it closes, so the script that
# was supposed to then type a path into the dialog is itself frozen and can never do it. Multiple
# earlier UAT rounds burned hours on this (see UAT/findings/direct-play*.md, savestate.md,
# glitch-harvester*.md and the dozens of retry screenshots in UAT/screenshots/).
# The fix is not a cleverer Invoke() - it is to not open the dialog at all:
#     Load-BrokenNesRom -ProcessId $pid -Path 'C:\path\to\game.nes'
# POSTs to /api/emulator/load-rom, which calls MainForm.LoadRomFromApi -> LoadRomFile(path)
# directly on the UI thread. No file dialog is ever created. Same story for save states
# (/api/emulator/quick-save-state) and core switching (/api/cores/apply).
# If you must invoke a modal menu item anyway, do it from a background runspace/job you can
# abandon, never from the script's main thread.
#
# ---------------------------------------------------------------------------------------------
# Port discovery (matches Windows/webapi/WebApiServer.cs as actually implemented)
# ---------------------------------------------------------------------------------------------
# The server tries the historical fixed ports FIRST (42067 HTTP / 42068 HTTPS). If binding fails -
# normally because another BrokenNes instance already owns them - it falls back to OS-assigned
# ephemeral ports for BOTH. A single running instance therefore still lives on 42067 exactly as
# it always did; you just cannot ASSUME that any more.
#
# Once it is genuinely listening, each instance writes:
#     %LOCALAPPDATA%\BrokenNes\instances\<pid>.json
#     { "pid": <int>, "httpPort": <int>, "httpsPort": <int>, "startedUtc": "<ISO-8601 UTC>",
#       "exePath": "<string>" }
# deleted on graceful shutdown. Files from crashed processes DO linger, so Get-BrokenNesInstances
# below drops any whose PID is no longer alive rather than trusting the directory listing.
#
# ALWAYS scope calls to a PID (`-ProcessId`), never to a bare port. Under parallel UAT there can
# be many live instances and hitting the wrong one silently corrupts another agent's run - the
# 2026-08-29 sweep caught exactly that happening through the old hardcoded port.
#
# Inside the app, WebView2 pages get their own instance's base URL as window.BROKENNES_API_BASE
# (injected via AddScriptToExecuteOnDocumentCreatedAsync); that is the in-app equivalent of what
# this file does from the outside.
#
# ---------------------------------------------------------------------------------------------
# Endpoint shapes are NOT guessed - every wrapper here was written against the route registration
# in Windows/webapi/WebApiServer.Endpoints.*.cs. Notable real-world details:
#   - Success is NOT signalled by HTTP status alone. Handlers return 200 with { success: false }
#     in some paths and 400 with { success: false, error: "..." } in others, so always look at
#     the .success field. Invoke-BrokenNesApi surfaces the parsed body either way.
#   - /api/ppu/framebuffer returns `data` as a BASE64 STRING (it is a C# byte[] and System.Text.Json
#     base64-encodes byte[]), 256*240*4 = 245760 bytes of RGBA. By contrast /api/memory/peek-range
#     deliberately converts to int[] first, so THAT one is a JSON array of numbers. Same server,
#     two different encodings - do not assume.
#   - /api/rtc/*, /api/gh/*, /api/timejump/*, /api/imagine/* sit behind a progression gate
#     (WebApiServer.ProgressionGate.cs) and answer 403 { success:false, error:"... is locked" }
#     when the corresponding webmodule is not unlocked in the save.
#   - DANGER: POST /api/save/reset wipes progression back to a fresh canonical save. Never call it
#     casually on a dev machine - this repo's save is a real, heavily-progressed one.
#
# Like UiaHelpers.ps1 this is a side-project test harness. Nothing shipped references it.
#
# One more thing the API cannot bootstrap past on its own: a modal MessageBox shown at startup
# ("Audio Warning") blocks the UI thread BEFORE the server is started, so nothing binds until it
# is clicked. Wait-BrokenNesApi clears those while polling - see "Startup modal dialogs" below.
#
# Usage:
#     . "$PSScriptRoot\ApiClient.ps1"
#     $inst = Start-BrokenNesInstance
#     Invoke-BrokenNesApi -ProcessId $inst.ProcessId -Method POST -Path '/api/navigation/go-to-emulator'
#     Load-BrokenNesRom -ProcessId $inst.ProcessId -Path 'C:\Users\philt\Desktop\game.nes'
#     Get-BrokenNesFramebuffer -ProcessId $inst.ProcessId -OutPng "$PSScriptRoot\..\screenshots\fb.png"
#     Stop-BrokenNesInstance -ProcessId $inst.ProcessId

Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------------------------------------
# Module-scope configuration
# ---------------------------------------------------------------------------------------------

# Discovery directory, per WebApiServer.GetInstancesDirectory().
$script:BrokenNesInstancesDir = Join-Path $env:LOCALAPPDATA 'BrokenNes\instances'

# The historical fixed ports. Still preferred by the server; only ever used here as an explicit
# opt-in fallback (-AllowLegacyPort), never silently.
$script:BrokenNesPreferredHttpPort = 42067
$script:BrokenNesPreferredHttpsPort = 42068
$script:BrokenNesLegacyBaseUrl = "http://127.0.0.1:$script:BrokenNesPreferredHttpPort"

$script:BrokenNesDefaultExe = 'C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\Windows\bin\Release\net10.0-windows\win-x64\BrokenNes.Windows.exe'

# pid -> base url, so a long test run does not re-read the discovery file on every single call.
$script:BrokenNesBaseUrlCache = @{}

# NES screen geometry, fixed by the PPU cores (ScreenWidth/ScreenHeight, RGBA 4 bytes per pixel).
$script:BrokenNesScreenWidth = 256
$script:BrokenNesScreenHeight = 240


# ---------------------------------------------------------------------------------------------
# Instance discovery
# ---------------------------------------------------------------------------------------------

# Reads %LOCALAPPDATA%\BrokenNes\instances and returns one object per LIVE instance.
# Discovery files left behind by processes that died (crash, kill) are dropped unless you pass
# -IncludeStale, because the ports they name may since have been handed to something else.
function Get-BrokenNesInstances {
    [CmdletBinding()]
    param(
        [int]$ProcessId = 0,
        [switch]$IncludeStale
    )

    if (-not (Test-Path -LiteralPath $script:BrokenNesInstancesDir)) { return @() }

    $result = @()
    foreach ($file in (Get-ChildItem -LiteralPath $script:BrokenNesInstancesDir -Filter '*.json' -ErrorAction SilentlyContinue)) {
        $json = $null
        try { $json = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json } catch { continue }
        if (-not $json -or -not $json.pid -or -not $json.httpPort) { continue }

        $pidValue = [int]$json.pid
        if ($ProcessId -gt 0 -and $pidValue -ne $ProcessId) { continue }

        # A file is only trustworthy if its PID is still running. (The server prunes these on
        # startup too, but only for processes it can prove are gone.)
        $proc = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
        $alive = ($null -ne $proc)
        if (-not $alive -and -not $IncludeStale) { continue }

        $result += [PSCustomObject]@{
            ProcessId    = $pidValue
            HttpPort     = [int]$json.httpPort
            HttpsPort    = [int]$json.httpsPort
            BaseUrl      = "http://127.0.0.1:$([int]$json.httpPort)"
            HttpsBaseUrl = "https://127.0.0.1:$([int]$json.httpsPort)"
            StartedUtc   = $json.startedUtc
            ExePath      = $json.exePath
            InstanceFile = $file.FullName
            IsAlive      = $alive
            UsingFallbackPorts = ([int]$json.httpPort -ne $script:BrokenNesPreferredHttpPort)
        }
    }

    return @($result | Sort-Object StartedUtc)
}

# Reports every LIVE BrokenNes instance that is NOT one of yours. Use this instead of the
# "Get-Process -Name BrokenNes.Windows | Stop-Process -Force" clean-slate reflex: it gives you the
# same information (am I alone on this machine?) without destroying another agent's run.
#
# Returns the foreign instances. With -Strict it throws instead, for a test that genuinely cannot
# share the machine - so the run fails loudly up front rather than dying halfway with a symptom
# that looks like a product crash.
function Assert-BrokenNesNoForeignInstances {
    [CmdletBinding()]
    param(
        [int[]]$MineProcessId = @(),
        [switch]$Strict
    )

    $foreign = @(Get-BrokenNesInstances | Where-Object { $MineProcessId -notcontains $_.ProcessId })

    # Instances that never published a discovery file (still starting, or blocked on a startup
    # dialog) would be invisible above, so also look at the raw process list.
    $knownPids = @($foreign | ForEach-Object { $_.ProcessId }) + $MineProcessId
    $bare = @(Get-Process -Name 'BrokenNes.Windows' -ErrorAction SilentlyContinue |
              Where-Object { $knownPids -notcontains $_.Id } |
              ForEach-Object { [PSCustomObject]@{ ProcessId = $_.Id; HttpPort = $null; BaseUrl = '<not published yet>' } })
    $foreign += $bare

    if ($foreign.Count -gt 0) {
        $ids = ($foreign | ForEach-Object { $_.ProcessId }) -join ', '
        $msg = "$($foreign.Count) foreign BrokenNes instance(s) are running (PIDs: $ids). " +
               "Do NOT kill them - they belong to another agent. Scope every call to your own PID, " +
               "and expect port 42067 to already be taken."
        if ($Strict) { throw $msg }
        Write-Warning $msg
    }

    return $foreign
}

# Resolves the base URL for one instance. This is what every other function calls, so a PID that
# never published a discovery file fails loudly here rather than quietly talking to some other
# instance on 42067. Pass -AllowLegacyPort only when you deliberately want that old behaviour.
function Resolve-BrokenNesBaseUrl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [switch]$AllowLegacyPort,
        [switch]$NoCache
    )

    if (-not $NoCache -and $script:BrokenNesBaseUrlCache.ContainsKey($ProcessId)) {
        return $script:BrokenNesBaseUrlCache[$ProcessId]
    }

    $inst = @(Get-BrokenNesInstances -ProcessId $ProcessId) | Select-Object -First 1
    if ($inst) {
        $script:BrokenNesBaseUrlCache[$ProcessId] = $inst.BaseUrl
        return $inst.BaseUrl
    }

    if ($AllowLegacyPort) { return $script:BrokenNesLegacyBaseUrl }

    throw "No live BrokenNes discovery file for PID $ProcessId in $script:BrokenNesInstancesDir. " +
          "The instance may still be starting (use Wait-BrokenNesApi), may have failed to bind, or may be dead."
}

function Clear-BrokenNesBaseUrlCache {
    [CmdletBinding()]
    param([int]$ProcessId = 0)
    if ($ProcessId -gt 0) { $script:BrokenNesBaseUrlCache.Remove($ProcessId) | Out-Null }
    else { $script:BrokenNesBaseUrlCache = @{} }
}


# ---------------------------------------------------------------------------------------------
# Startup modal dialogs - the one thing that MUST be handled outside the API
# ---------------------------------------------------------------------------------------------
#
# FIXED 2026-09-03 - this dialog handling is now belt-and-braces, not a hard requirement.
#
# The original chicken-and-egg (confirmed live 2026-09-02): MainForm.Initialization.cs showed a
# blocking MessageBox ("Audio Warning", MB_OK) when AudioManager failed to initialize, and only
# called EnsureWebApiServerRunningAsync() AFTER it. MessageBox.Show blocks the UI thread, so on a
# machine with no audio device the Web API never bound until a human clicked OK - /api/health just
# timed out and no discovery file was ever written. The "DirectX initialization failed" box had the
# same shape.
#
# That is fixed in the app: the API server now starts as the FIRST statement of InitializeEmulator(),
# and the advisory dialogs were moved onto their own background STA thread so they never park the
# message loop. Verified: the full API surface answers with the Audio Warning box still open and
# unclicked (/api/health green in ~1.2s across 5 cold launches). Setting BROKENNES_NO_DIALOGS=1
# suppresses the advisory pop-ups entirely for unattended runs.
#
# Wait-BrokenNesApi still clears such dialogs while it polls (pass -KeepDialogs to opt out), because
# it costs nothing and keeps the screen clean for screenshot-based checks - but it is no longer what
# makes the API reachable. This uses plain Win32, not UI Automation: FindWindowEx
# enumerates top-level "#32770" dialog windows, and the OK button is dismissed with a POSTED
# BM_CLICK. Posting matters - a SendMessage into a modal dialog's thread, like UIA's
# InvokePattern.Invoke(), can block the caller.
try {
    Add-Type -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr FindWindowEx(System.IntPtr parent, System.IntPtr childAfter, string className, string windowName);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern int GetWindowText(System.IntPtr hWnd, System.Text.StringBuilder text, int count);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool PostMessage(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
'@ -Name Win32Dialogs -Namespace BrokenNesUat -ErrorAction Stop
} catch { }  # already defined by an earlier dot-source in this session

function Get-BrokenNesWindowText {
    param([IntPtr]$Hwnd)
    $sb = New-Object System.Text.StringBuilder 512
    [void][BrokenNesUat.Win32Dialogs]::GetWindowText($Hwnd, $sb, $sb.Capacity)
    return $sb.ToString()
}

# Lists the visible modal dialogs ("#32770") currently owned by an instance, with their buttons.
function Get-BrokenNesDialogs {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)

    $found = @()
    $hwnd = [IntPtr]::Zero
    while ($true) {
        # [NullString]::Value, NOT $null: PowerShell binds $null to a [string] parameter as
        # String.Empty, which makes FindWindowEx search for a window literally titled "" and
        # return nothing at all. That silently breaks the whole enumeration.
        $hwnd = [BrokenNesUat.Win32Dialogs]::FindWindowEx([IntPtr]::Zero, $hwnd, '#32770', [NullString]::Value)
        if ($hwnd -eq [IntPtr]::Zero) { break }

        $owner = [uint32]0
        [void][BrokenNesUat.Win32Dialogs]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ([int]$owner -ne $ProcessId) { continue }
        if (-not [BrokenNesUat.Win32Dialogs]::IsWindowVisible($hwnd)) { continue }

        $buttons = @()
        $child = [IntPtr]::Zero
        while ($true) {
            $child = [BrokenNesUat.Win32Dialogs]::FindWindowEx($hwnd, $child, 'Button', [NullString]::Value)
            if ($child -eq [IntPtr]::Zero) { break }
            $buttons += [PSCustomObject]@{ Hwnd = $child; Text = (Get-BrokenNesWindowText -Hwnd $child) }
        }

        $found += [PSCustomObject]@{
            Hwnd    = $hwnd
            Title   = (Get-BrokenNesWindowText -Hwnd $hwnd)
            Buttons = $buttons
        }
    }
    return @($found)
}

# Clicks a button (default OK) on every modal dialog the instance currently owns.
# Returns the dialog titles it acted on.
function Dismiss-BrokenNesDialogs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$Button = 'OK'
    )

    $BM_CLICK = 0x00F5
    $dismissed = @()
    foreach ($dlg in (Get-BrokenNesDialogs -ProcessId $ProcessId)) {
        # Button labels carry an accelerator ampersand ("&OK"), so match on the stripped text.
        $target = $dlg.Buttons | Where-Object { ($_.Text -replace '&','') -eq $Button } | Select-Object -First 1
        if (-not $target) { $target = $dlg.Buttons | Select-Object -First 1 }
        if (-not $target) { continue }
        [void][BrokenNesUat.Win32Dialogs]::PostMessage($target.Hwnd, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
        $dismissed += $dlg.Title
    }
    return @($dismissed)
}


# ---------------------------------------------------------------------------------------------
# Core request plumbing
# ---------------------------------------------------------------------------------------------

# The one call every wrapper goes through.
#   -Path      route beginning with '/api/...'; may already contain a query string
#   -Query     hashtable merged into the query string, URL-encoded for you
#   -Body      hashtable/object serialized as JSON for POST/PUT (property names must match the
#              C# request DTO in WebApiModels.cs; System.Text.Json's default binding is
#              case-insensitive, so 'path' and 'Path' both work)
#   -Raw       return the parsed body PLUS the HTTP status, instead of just the body
# Non-2xx responses are NOT thrown: the server puts its real error text in a JSON body
# ({ success:false, error:"..." }) for 400s and for the 403 progression gate, and that body is
# far more useful than an exception. Check .success on what you get back.
function Invoke-BrokenNesApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [ValidateSet('GET','POST','PUT','DELETE')][string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Query,
        $Body,
        [int]$TimeoutSec = 30,
        [switch]$Raw,
        [switch]$AllowLegacyPort
    )

    $baseUrl = Resolve-BrokenNesBaseUrl -ProcessId $ProcessId -AllowLegacyPort:$AllowLegacyPort
    $uri = $baseUrl + $Path

    if ($Query -and $Query.Count -gt 0) {
        $pairs = foreach ($k in $Query.Keys) {
            '{0}={1}' -f [uri]::EscapeDataString([string]$k), [uri]::EscapeDataString([string]$Query[$k])
        }
        # .Contains, NOT -like '*?*': in a -like pattern '?' is a single-character WILDCARD, so
        # that test is true for every non-empty path and every query would start with '&'.
        $uri += ($(if ($Path.Contains('?')) { '&' } else { '?' }) + ($pairs -join '&'))
    }

    $reqArgs = @{
        Uri             = $uri
        Method          = $Method
        TimeoutSec      = $TimeoutSec
        UseBasicParsing = $true
        ErrorAction     = 'Stop'
    }
    if ($null -ne $Body) {
        $reqArgs.Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
        $reqArgs.ContentType = 'application/json'
    }
    # PowerShell 7 can hand back 4xx/5xx responses instead of throwing; 5.1 cannot, so there is a
    # catch below that digs the body out of the exception's response stream.
    if ($PSVersionTable.PSVersion.Major -ge 6) { $reqArgs.SkipHttpErrorCheck = $true }

    $status = 0
    $content = $null
    try {
        $response = Invoke-WebRequest @reqArgs
        $status = [int]$response.StatusCode
        $content = $response.Content
    }
    catch {
        $ex = $_.Exception
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $content = $_.ErrorDetails.Message }
        if ($ex.Response) {
            try { $status = [int]$ex.Response.StatusCode } catch { }
            if (-not $content) {
                try {
                    $stream = $ex.Response.GetResponseStream()
                    $reader = New-Object System.IO.StreamReader($stream)
                    $content = $reader.ReadToEnd()
                    $reader.Dispose()
                } catch { }
            }
        }
        if (-not $content) { throw }
    }

    $parsed = $null
    if ($content) {
        try { $parsed = $content | ConvertFrom-Json } catch { $parsed = $content }
    }

    if ($Raw) {
        return [PSCustomObject]@{ StatusCode = $status; Content = $content; Body = $parsed; Uri = $uri }
    }
    return $parsed
}

# GET /api/health - the only endpoint that answers before a ROM/NES exists, so it is the readiness probe.
function Test-BrokenNesApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [int]$TimeoutSec = 3
    )
    try {
        $r = Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/health' -TimeoutSec $TimeoutSec
        return [bool]$r.success
    } catch { return $false }
}

# Blocks until the instance has (a) published a discovery file and (b) answered /api/health.
# Returns the instance object on success; throws on timeout, or immediately if the process dies.
#
# While polling it also clears startup MessageBoxes (see Dismiss-BrokenNesDialogs above): on this
# machine the app reliably raises an "Audio Warning" MB_OK box, and because that box blocks the UI
# thread BEFORE the API server is started, nothing ever binds until it is clicked. Pass
# -KeepDialogs if a test needs to inspect the dialog itself.
function Wait-BrokenNesApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [int]$TimeoutSeconds = 90,
        [int]$PollMilliseconds = 400,
        [switch]$KeepDialogs
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $dialogsDismissed = @()
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "BrokenNes PID $ProcessId exited before its Web API became ready."
        }

        Clear-BrokenNesBaseUrlCache -ProcessId $ProcessId
        $inst = @(Get-BrokenNesInstances -ProcessId $ProcessId) | Select-Object -First 1
        if ($inst) {
            if (Test-BrokenNesApi -ProcessId $ProcessId) {
                if ($dialogsDismissed.Count -gt 0) {
                    Write-Verbose "Dismissed startup dialog(s) while waiting: $($dialogsDismissed -join ', ')"
                }
                return $inst
            }
        }
        elseif (-not $KeepDialogs) {
            $dialogsDismissed += (Dismiss-BrokenNesDialogs -ProcessId $ProcessId)
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    }

    $pending = if ($KeepDialogs) { @(Get-BrokenNesDialogs -ProcessId $ProcessId | ForEach-Object { $_.Title }) } else { @() }
    $hint = if ($pending.Count -gt 0) { " Modal dialog(s) still open: $($pending -join ', ')." } else { '' }
    throw "Timed out after ${TimeoutSeconds}s waiting for BrokenNes PID $ProcessId to serve /api/health.$hint"
}

# Launches a fresh, independent instance and waits for its API. Returns the instance object
# (ProcessId / BaseUrl / HttpPort / HttpsPort / ...) plus the Process handle.
# Multiple instances CAN run at once; each gets its own ports and its own discovery file, so
# always keep the returned ProcessId and scope every later call to it.
# Startup MessageBoxes are cleared while waiting (see Wait-BrokenNesApi) unless -KeepDialogs.
function Start-BrokenNesInstance {
    [CmdletBinding()]
    param(
        [string]$ExePath = $script:BrokenNesDefaultExe,
        [string[]]$ArgumentList,
        [int]$TimeoutSeconds = 90,
        [switch]$NoWait,
        [switch]$KeepDialogs
    )

    if (-not (Test-Path -LiteralPath $ExePath)) { throw "BrokenNes executable not found: $ExePath" }

    $startArgs = @{ FilePath = $ExePath; PassThru = $true; WorkingDirectory = (Split-Path -Parent $ExePath) }
    if ($ArgumentList -and $ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }
    $proc = Start-Process @startArgs

    if ($NoWait) {
        return [PSCustomObject]@{ ProcessId = $proc.Id; Process = $proc; BaseUrl = $null }
    }

    $inst = Wait-BrokenNesApi -ProcessId $proc.Id -TimeoutSeconds $TimeoutSeconds -KeepDialogs:$KeepDialogs
    return [PSCustomObject]@{
        ProcessId          = $inst.ProcessId
        BaseUrl            = $inst.BaseUrl
        HttpsBaseUrl       = $inst.HttpsBaseUrl
        HttpPort           = $inst.HttpPort
        HttpsPort          = $inst.HttpsPort
        StartedUtc         = $inst.StartedUtc
        ExePath            = $inst.ExePath
        InstanceFile       = $inst.InstanceFile
        UsingFallbackPorts = $inst.UsingFallbackPorts
        Process            = $proc
    }
}

# Closes an instance. Tries the window-close path first so the app can run its normal shutdown
# (which deletes the discovery file); escalates to a kill if it will not go.
#
# ALWAYS PID-scoped, and deliberately so - see the process-name warning in this file's header.
# Prefer letting it use CloseMainWindow (i.e. do not reach for -Force by default): the graceful
# path is what runs OnFormClosing, which flushes battery RAM and the continue checkpoint. -Force
# is TerminateProcess and skips all of that.
function Stop-BrokenNesInstance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [int]$TimeoutSeconds = 20,
        [switch]$Force
    )

    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) {
        Remove-BrokenNesStaleInstanceFiles | Out-Null
        Clear-BrokenNesBaseUrlCache -ProcessId $ProcessId
        return [PSCustomObject]@{ ProcessId = $ProcessId; Stopped = $true; Method = 'already-exited' }
    }

    $method = 'CloseMainWindow'
    if (-not $Force) {
        try { $proc.CloseMainWindow() | Out-Null } catch { }
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 250
        }
    }

    if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        $method = 'Stop-Process -Force'
        try { Stop-Process -Id $ProcessId -Force -ErrorAction Stop } catch { }
        Start-Sleep -Milliseconds 500
    }

    $stopped = -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
    Clear-BrokenNesBaseUrlCache -ProcessId $ProcessId
    Remove-BrokenNesStaleInstanceFiles | Out-Null
    return [PSCustomObject]@{ ProcessId = $ProcessId; Stopped = $stopped; Method = $method }
}

# Deletes discovery files whose PID is provably gone. A force-killed instance never runs its
# ProcessExit hook, so it leaves one behind.
function Remove-BrokenNesStaleInstanceFiles {
    [CmdletBinding()]
    param()
    $removed = @()
    foreach ($inst in (Get-BrokenNesInstances -IncludeStale)) {
        if ($inst.IsAlive) { continue }
        try { Remove-Item -LiteralPath $inst.InstanceFile -Force -ErrorAction Stop; $removed += $inst.InstanceFile } catch { }
    }
    return $removed
}


# ---------------------------------------------------------------------------------------------
# Emulator control
# ---------------------------------------------------------------------------------------------

# POST /api/navigation/go-to-emulator - swap the shell from the WebView2 front end to the native
# emulator view. Body: none. Response: { success, mode:"Emulator" }.
# Do this before expecting to see gameplay; a ROM will load either way, but the WebView2 page
# stays on top of the D3D surface until the view mode changes.
function Show-BrokenNesEmulator {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/navigation/go-to-emulator'
}

# POST /api/emulator/load-rom - load a ROM BY ABSOLUTE PATH. No file dialog is involved: the
# handler calls MainForm.LoadRomFromApi -> LoadRomFile(path) on the UI thread.
# Body: { "Path": "<absolute path>" } (LoadRomRequest). Response: { success, path }.
# NOTE the server checks File.Exists on ITS OWN process, so the path must be valid for the app,
# and `success:false` with HTTP 200 is what you get for a missing/unloadable file.
function Load-BrokenNesRom {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutSec = 60
    )
    $full = $Path
    try { $full = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path } catch { }
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/emulator/load-rom' -Body @{ Path = $full } -TimeoutSec $TimeoutSec
}

# GET /api/emulator/current-rom -> { success, path, name, isTestRom }.
# isTestRom is true while the built-in test.nes placeholder is loaded, i.e. "no real ROM yet".
function Get-BrokenNesCurrentRom {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/emulator/current-rom'
}

# POST /api/emulator/close-rom - unload and fall back to the embedded test ROM.
function Close-BrokenNesRom {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/emulator/close-rom'
}

# POST /api/emulator/pause | /api/emulator/resume. No body. -> { success }.
function Suspend-BrokenNesEmulation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/emulator/pause'
}

function Resume-BrokenNesEmulation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/emulator/resume'
}

# POST /api/emulator/quick-save-state | quick-load-state - the F7/F5 equivalents.
# Response is { success, gated } - `gated:true` means the progression system refused, not that
# something crashed (save states are an unlockable in the desktop build).
function Save-BrokenNesQuickState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/emulator/quick-save-state'
}

function Restore-BrokenNesQuickState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/emulator/quick-load-state'
}


# ---------------------------------------------------------------------------------------------
# Cores, shaders, CPU state
# ---------------------------------------------------------------------------------------------

# GET /api/cpu/core | /api/ppu/core | /api/apu/core -> { success, coreId }.
# Returns the bare core id string (e.g. "FMC") for convenience; use -Full for the whole response.
function Get-BrokenNesCore {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][ValidateSet('cpu','ppu','apu')][string]$Kind,
        [switch]$Full
    )
    $r = Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path "/api/$Kind/core"
    if ($Full) { return $r }
    return $r.coreId
}

# GET /api/cpu/cores | /api/ppu/cores | /api/apu/cores -> { success, cores: [...] }.
# These are the cores the EMULATOR knows about; the desktop build's menus additionally hide any
# the progression save does not own, so this list can be longer than what the UI shows.
function Get-BrokenNesCores {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][ValidateSet('cpu','ppu','apu')][string]$Kind
    )
    return (Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path "/api/$Kind/cores").cores
}

# POST /api/cores/apply - hot-swap cores. Body: ApplyCoresRequest
#   { "CpuId":"FIX", "PpuId":"FIX", "ApuId":"FIX", "OverrideReason":"deck-enforced" }
# Any field may be omitted. Ids are upper-cased server-side.
#
# TRAP - do NOT reach for -OverrideReason by default. It routes to the bypassProgression setters,
# which hot-swap the core but do NOT persist it, so the very next ROM load calls
# ApplySavedCoreSelections(), re-reads config, and silently reverts to FMC. The symptom is a pure
# black screen with the CPU wandering in zero page - indistinguishable from the pre-2026-09-03
# FIX-is-unobtainable bug, and it cost one acceptance run a full test cycle before it was spotted.
# Call this WITHOUT -OverrideReason: that goes through the normal gated setter, which now succeeds
# for the FIX family (MainForm.Progression.cs AlwaysAvailableCoreIds) and persists to config.json,
# so the selection survives ROM reloads and process restarts.
# -OverrideReason exists for the game's own deck/cutscene enforcement; use it only when you are
# deliberately testing that transient-override behavior itself.
function Set-BrokenNesCores {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$CpuId,
        [string]$PpuId,
        [string]$ApuId,
        [ValidateSet('deck-enforced','story-cutscene')][string]$OverrideReason
    )
    $body = @{}
    if ($CpuId) { $body.CpuId = $CpuId }
    if ($PpuId) { $body.PpuId = $PpuId }
    if ($ApuId) { $body.ApuId = $ApuId }
    if ($OverrideReason) { $body.OverrideReason = $OverrideReason }
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/cores/apply' -Body $body
}

# GET /api/cores - full metadata catalogue: { cpu:[...], ppu:[...], apu:[...], clock:[...], shader:[...] }
# with id/name/description/performance/rating/category per entry. Note this one has NO `success`
# field on the happy path - it returns the object directly.
function Get-BrokenNesCoreCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/cores'
}

# POST /api/shader/set - Body: SetShaderRequest { "ShaderName":"VHS", "OverrideReason":null }.
# GET /api/shader/current for the active one.
function Set-BrokenNesShader {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$ShaderName,
        [ValidateSet('deck-enforced','story-cutscene')][string]$OverrideReason
    )
    $body = @{ ShaderName = $ShaderName }
    if ($OverrideReason) { $body.OverrideReason = $OverrideReason }
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/shader/set' -Body $body
}

function Get-BrokenNesShader {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/shader/current'
}

# GET /api/cpu/registers -> { success, registers:{ PC,A,X,Y,P,SP } }.
# The values are pre-formatted HEX STRINGS ("0xC04F"), not numbers - the server does the
# formatting. Convert with [Convert]::ToInt32($r.registers.PC,16) if you need arithmetic.
function Get-BrokenNesCpuRegisters {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [switch]$Full
    )
    $r = Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/cpu/registers'
    if ($Full) { return $r }
    return $r.registers
}


# ---------------------------------------------------------------------------------------------
# Memory access
# ---------------------------------------------------------------------------------------------

# GET /api/memory/domains -> { success, domains:[ { name, size, description } ] }.
# Domain names contain SPACES ("System RAM", "CPU Bus", "PRG ROM", "PRG RAM", "CHR") and are used
# verbatim as query values - Invoke-BrokenNesApi URL-encodes them for you.
function Get-BrokenNesMemoryDomains {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return (Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/memory/domains').domains
}

# Reads memory.
#   -Length omitted -> GET /api/memory/peek        -> returns a single [int]
#   -Length given   -> GET /api/memory/peek-range  -> returns [int[]]
# peek-range's `data` is a JSON array of numbers (the handler converts byte[] to int[] on purpose
# so it is not base64'd). Pass -Full for the whole response object instead of just the bytes.
function Get-BrokenNesMemory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$Domain,
        [Parameter(Mandatory)][int]$Address,
        [int]$Length = 0,
        [switch]$Full
    )

    if ($Length -le 0) {
        $r = Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/memory/peek' -Query @{ domain = $Domain; address = $Address }
        if ($Full) { return $r }
        if (-not $r.success) { throw "peek failed: $($r.error)" }
        return [int]$r.value
    }

    $r = Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/memory/peek-range' -Query @{ domain = $Domain; address = $Address; length = $Length }
    if ($Full) { return $r }
    if (-not $r.success) { throw "peek-range failed: $($r.error)" }
    return @($r.data)
}

# Writes memory.
#   -Value -> POST /api/memory/poke        Body PokeRequest { Domain, Address, Value }
#             The response echoes beforeValue/afterValue/verified, which is the cheapest way to
#             tell a real write from one the mapper swallowed (PRG ROM writes often no-op).
#   -Data  -> POST /api/memory/poke-range  Body PokeRangeRequest { Domain, Address, Data:int[] }
function Set-BrokenNesMemory {
    [CmdletBinding(DefaultParameterSetName = 'Single')]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$Domain,
        [Parameter(Mandatory)][int]$Address,
        [Parameter(Mandatory, ParameterSetName = 'Single')][int]$Value,
        [Parameter(Mandatory, ParameterSetName = 'Range')][int[]]$Data
    )

    if ($PSCmdlet.ParameterSetName -eq 'Single') {
        return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/memory/poke' -Body @{ Domain = $Domain; Address = $Address; Value = $Value }
    }
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/memory/poke-range' -Body @{ Domain = $Domain; Address = $Address; Data = @($Data) }
}


# ---------------------------------------------------------------------------------------------
# Framebuffer
# ---------------------------------------------------------------------------------------------

# GET /api/ppu/framebuffer -> { success, width:256, height:240, format:"RGBA", data:<base64> }.
#
# `data` is a C# byte[] and System.Text.Json base64-encodes byte[], so it arrives as a STRING,
# not an array (unlike /api/memory/peek-range, which is deliberately int[]). Both encodings are
# handled here anyway so a future server change cannot silently break this.
#
# Byte order is R,G,B,A per pixel (see PPU_*.cs: frameBuffer[fi+0]=r ... [fi+3]=255). GDI+'s
# Format32bppArgb is B,G,R,A in memory, so the channels are swapped while blitting.
#
# Returns geometry plus cheap "is this a real picture?" statistics, and writes a PNG when -OutPng
# is given. A genuinely blank/black frame has UniqueColors = 1; a running game is normally dozens.
function Get-BrokenNesFramebuffer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [string]$OutPng,
        [int]$TimeoutSec = 60
    )

    $r = Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/ppu/framebuffer' -TimeoutSec $TimeoutSec
    if (-not $r.success) { throw "framebuffer request failed: $($r.error)" }

    $bytes = $null
    if ($r.data -is [string]) { $bytes = [Convert]::FromBase64String($r.data) }
    elseif ($null -ne $r.data) { $bytes = [byte[]]@($r.data | ForEach-Object { [byte]$_ }) }
    else { throw "framebuffer response contained no data" }

    $width = if ($r.width) { [int]$r.width } else { $script:BrokenNesScreenWidth }
    $height = if ($r.height) { [int]$r.height } else { $script:BrokenNesScreenHeight }
    $expected = $width * $height * 4
    if ($bytes.Length -ne $expected) {
        throw "framebuffer size mismatch: got $($bytes.Length) bytes, expected $expected for ${width}x${height} RGBA"
    }

    # Statistics first (cheap, and useful even when no PNG is wanted).
    $colors = New-Object 'System.Collections.Generic.HashSet[int]'
    $nonBlack = 0
    for ($i = 0; $i -lt $bytes.Length; $i += 4) {
        $rgb = ([int]$bytes[$i] -shl 16) -bor ([int]$bytes[$i + 1] -shl 8) -bor [int]$bytes[$i + 2]
        [void]$colors.Add($rgb)
        if ($rgb -ne 0) { $nonBlack++ }
    }

    $savedPath = $null
    if ($OutPng) {
        $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            # RGBA (source) -> BGRA (GDI+ Format32bppArgb memory layout).
            $bgra = New-Object byte[] $bytes.Length
            for ($i = 0; $i -lt $bytes.Length; $i += 4) {
                $bgra[$i]     = $bytes[$i + 2]
                $bgra[$i + 1] = $bytes[$i + 1]
                $bgra[$i + 2] = $bytes[$i]
                $bgra[$i + 3] = 255
            }
            # Stride can exceed width*4; copy row by row rather than in one blit.
            for ($y = 0; $y -lt $height; $y++) {
                $dest = [IntPtr]::Add($data.Scan0, $y * $data.Stride)
                [System.Runtime.InteropServices.Marshal]::Copy($bgra, $y * $width * 4, $dest, $width * 4)
            }
        }
        finally { $bmp.UnlockBits($data) }

        $dir = Split-Path -Parent $OutPng
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $savedPath = (Resolve-Path -LiteralPath $OutPng).Path
    }

    return [PSCustomObject]@{
        Width           = $width
        Height          = $height
        Format          = $r.format
        ByteCount       = $bytes.Length
        Bytes           = $bytes
        PixelCount      = $width * $height
        UniqueColors    = $colors.Count
        NonBlackPixels  = $nonBlack
        NonBlackPercent = [math]::Round(100.0 * $nonBlack / ($width * $height), 2)
        IsBlank         = ($colors.Count -le 1)
        OutPng          = $savedPath
    }
}


# ---------------------------------------------------------------------------------------------
# Corruption (progression-gated: 403 { success:false, error:"RTC + Glitch Harvester is locked" })
# ---------------------------------------------------------------------------------------------

# POST /api/rtc/blast - one corruption blast with the corruptor's CURRENT domain selection,
# intensity and blast type. No body. -> { success, message, writesApplied }.
# Configure first with POST /api/rtc/intensity { Intensity:int },
# POST /api/rtc/blast-type { BlastType:"..." }, POST /api/rtc/domains/selection { SelectedDomains:[...] }.
function Invoke-BrokenNesBlast {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/rtc/blast'
}

# POST /api/rtc/auto-corrupt - Body AutoCorruptRequest { "Enabled": true|false }.
function Set-BrokenNesAutoCorrupt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][bool]$Enabled
    )
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method POST -Path '/api/rtc/auto-corrupt' -Body @{ Enabled = $Enabled }
}

# GET /api/input/button-event - polls for the most recent webmodule X/Y button event and CLEARS
# it. Only events younger than 100ms are reported, so this is a poll-fast-or-miss-it endpoint;
# it is how webmodules see the overlay OVERLAY buttons - it is NOT how you press a NES button.
# To inject actual gameplay input, use POST /api/input/set-buttons / /api/input/clear and read it
# back with GET /api/input/state (added 2026-09-03). Injection is merged additively with real
# keyboard/gamepad polling, so it can never mask a physically held key.
function Get-BrokenNesButtonEvent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    return Invoke-BrokenNesApi -ProcessId $ProcessId -Method GET -Path '/api/input/button-event'
}
