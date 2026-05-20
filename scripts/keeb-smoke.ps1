# Keeb TKL smoke test - exercises every keeb endpoint against a running
# qos-service on a Windows host with a physical Keeb TKL attached.
# Validates state shape, layer round-trip, firmware lighting writes,
# passive lighting writes, game mode writes, rotary sensitivity, and
# macro round-trip. Intended for T1 (192.168.1.110).
#
# Usage:  powershell -File keeb-smoke.ps1
# Assumes the service is already running on http://localhost:9400.

$ErrorActionPreference = "SilentlyContinue"
$Base = "http://localhost:9400"
$Script:Passed = 0
$Script:Failed = 0
$Script:Warned = 0

function Get-AuthToken {
    try { (Invoke-RestMethod -Uri "$Base/pair" -UseBasicParsing).token }
    catch { $null }
}

function Get-AuthHeader([string]$Token) {
    @{ Authorization = "Bearer $Token"; "Content-Type" = "application/json" }
}

function Write-Pass([string]$Message) { Write-Host "  PASS $Message" -ForegroundColor Green; $Script:Passed++ }
function Write-Fail([string]$Message) { Write-Host "  FAIL $Message" -ForegroundColor Red;   $Script:Failed++ }
function Write-Warn2([string]$Message) { Write-Host "  WARN $Message" -ForegroundColor Yellow; $Script:Warned++ }

$Token = Get-AuthToken
if (-not $Token) {
    Write-Host "FATAL: could not pair with service at $Base" -ForegroundColor Red
    exit 1
}
Write-Host "Paired (token $($Token.Substring(0,8))...)"
Write-Host ""

# 1. Connection check
Write-Host "[1] Device connectivity"
try {
    $state = Invoke-RestMethod -Uri "$Base/keeb/state" -Headers (Get-AuthHeader $Token) -UseBasicParsing
    if ($state.isConnected) {
        Write-Pass "Keeb is connected (layout=$($state.layout), profile=$($state.profile))"
    } else {
        Write-Warn2 "Keeb reported not connected - most subsequent tests will be offline-only"
    }
} catch {
    Write-Fail "/keeb/state errored: $($_.Exception.Message)"
}

# 2. Each layer returns key data
Write-Host ""
Write-Host "[2] Layer round-trip"
foreach ($layer in 0..3) {
    try {
        $r = Invoke-RestMethod -Uri "$Base/keeb/layer/$layer" -Headers (Get-AuthHeader $Token) -UseBasicParsing
        $rowCount = $r.keys.Count
        if ($rowCount -gt 0 -or -not $state.isConnected) {
            Write-Pass "GET /keeb/layer/$layer returned $rowCount rows"
        } else {
            Write-Fail "GET /keeb/layer/$layer returned 0 rows (expected device key map)"
        }
    } catch {
        Write-Fail "GET /keeb/layer/$layer errored: $($_.Exception.Message)"
    }
}

# 3. Write a key on layer 0 and read it back
Write-Host ""
Write-Host "[3] Single-key write"
$writeBody = @{ x = 2; y = 5; func = "Macro1"; mode = "MacroKey"; input = 1 } | ConvertTo-Json
try {
    $resp = Invoke-RestMethod -Uri "$Base/keeb/layer/0/key" -Method Post -Body $writeBody -Headers (Get-AuthHeader $Token) -UseBasicParsing
    $written = $resp.keys[2][5]
    if ($written.function -eq "Macro1") {
        Write-Pass "POST /keeb/layer/0/key Macro1 at (2,5) round-tripped"
    } else {
        Write-Fail "Wrote Macro1 at (2,5) but readback shows '$($written.function)'"
    }
} catch {
    Write-Fail "POST /keeb/layer/0/key errored: $($_.Exception.Message)"
}

# 4. Reset the layer
Write-Host ""
Write-Host "[4] Layer reset"
try {
    Invoke-RestMethod -Uri "$Base/keeb/layer/0/reset" -Method Post -Body "{}" -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
    Write-Pass "POST /keeb/layer/0/reset accepted"
} catch {
    Write-Fail "POST /keeb/layer/0/reset errored: $($_.Exception.Message)"
}

# 5. Firmware lighting
Write-Host ""
Write-Host "[5] Firmware lighting writes"
foreach ($mode in @("Static","Breathe","Rainbow","Wave","Flow","PingPong")) {
    $body = @{ animationMode = $mode; speed = "Standard"; direction = "LeftToRight"; brightness = 70; keyIndicator = $true } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$Base/keeb/firmware/lighting" -Method Post -Body $body -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
        Write-Pass "Effect $mode applied"
    } catch {
        Write-Fail "Effect $mode errored: $($_.Exception.Message)"
    }
    Start-Sleep -Milliseconds 250
}

# 6. Passive (key-reactive) lighting
Write-Host ""
Write-Host "[6] Passive lighting toggle"
$onBody = @{ keyReactive = $true; keyReactiveMask = $true; keyReactiveMode = "Ripple"; keyReactiveColor = @{ r = 200; g = 100; b = 50; a = 255 } } | ConvertTo-Json -Depth 3
try {
    Invoke-RestMethod -Uri "$Base/keeb/passive-lighting" -Method Post -Body $onBody -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
    Write-Pass "Passive lighting ON (Ripple mode) accepted"
} catch {
    Write-Fail "Passive lighting ON errored: $($_.Exception.Message)"
}
Start-Sleep -Milliseconds 250
$offBody = @{ keyReactive = $false; keyReactiveMask = $false; keyReactiveMode = "Off"; keyReactiveColor = @{ r = 0; g = 0; b = 0; a = 255 } } | ConvertTo-Json -Depth 3
try {
    Invoke-RestMethod -Uri "$Base/keeb/passive-lighting" -Method Post -Body $offBody -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
    Write-Pass "Passive lighting OFF accepted"
} catch {
    Write-Fail "Passive lighting OFF errored: $($_.Exception.Message)"
}

# 7. Game mode
Write-Host ""
Write-Host "[7] Game mode toggle"
$gmOn = @{ altF4 = $true; altTab = $true; shiftTab = $true; windowsKey = $true } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "$Base/keeb/game-mode" -Method Post -Body $gmOn -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
    Write-Pass "Game mode ON (all lockouts) accepted"
} catch {
    Write-Fail "Game mode ON errored: $($_.Exception.Message)"
}
$gmOff = @{ altF4 = $false; altTab = $false; shiftTab = $false; windowsKey = $false } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "$Base/keeb/game-mode" -Method Post -Body $gmOff -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
    Write-Pass "Game mode OFF accepted"
} catch {
    Write-Fail "Game mode OFF errored: $($_.Exception.Message)"
}

# 8. Rotary sensitivity sweep
Write-Host ""
Write-Host "[8] Rotary sensitivity"
foreach ($s in @("Slow","Steady","Balanced","Fast","Turbo")) {
    $body = @{ sensitivity = $s } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$Base/keeb/rotary/sensitivity" -Method Post -Body $body -Headers (Get-AuthHeader $Token) -UseBasicParsing | Out-Null
        Write-Pass "Sensitivity $s accepted"
    } catch {
        Write-Fail "Sensitivity $s errored: $($_.Exception.Message)"
    }
}

# 9. Macro round-trip
Write-Host ""
Write-Host "[9] Macro write+read"
$macroBody = @{
    keys = @(
        @{ key = "A"; type = "Make";  duration = 50; category = "" }
        @{ key = "A"; type = "Break"; duration = 50; category = "" }
        @{ key = "B"; type = "Make";  duration = 100; category = "" }
        @{ key = "B"; type = "Break"; duration = 100; category = "" }
    )
} | ConvertTo-Json -Depth 3
try {
    $set = Invoke-RestMethod -Uri "$Base/keeb/macro/0" -Method Post -Body $macroBody -Headers (Get-AuthHeader $Token) -UseBasicParsing
    if ($set.macro.keys.Count -ge 4) {
        Write-Pass "Macro write returned $($set.macro.keys.Count) keystrokes"
    } else {
        Write-Warn2 "Macro write returned only $($set.macro.keys.Count) keystrokes (expected >=4)"
    }
} catch {
    Write-Fail "Macro write errored: $($_.Exception.Message)"
}
try {
    $get = Invoke-RestMethod -Uri "$Base/keeb/macro/0" -Headers (Get-AuthHeader $Token) -UseBasicParsing
    if ($get.macro.keys.Count -ge 4) {
        Write-Pass "Macro read returned $($get.macro.keys.Count) keystrokes"
    } else {
        Write-Warn2 "Macro read returned $($get.macro.keys.Count) keystrokes"
    }
} catch {
    Write-Fail "Macro read errored: $($_.Exception.Message)"
}

# 10. Settings final check
Write-Host ""
Write-Host "[10] Settings shape"
try {
    $s = Invoke-RestMethod -Uri "$Base/keeb/settings" -Headers (Get-AuthHeader $Token) -UseBasicParsing
    if ($s.PSObject.Properties["animationMode"] -and $s.PSObject.Properties["keyReactive"]) {
        Write-Pass "GET /keeb/settings returns split firmware + passive fields"
    } else {
        Write-Fail "GET /keeb/settings missing expected fields"
    }
} catch {
    Write-Fail "GET /keeb/settings errored: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "============================================"
Write-Host "Results: $Script:Passed passed, $Script:Failed failed, $Script:Warned warned"
Write-Host "============================================"
exit $Script:Failed
