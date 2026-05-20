# Keeb TKL smoke test — exercises every keeb endpoint against a running
# qos-service on a Windows host with a physical Keeb TKL attached.
# Validates state shape, layer round-trip, firmware lighting writes,
# passive lighting writes, game mode writes, rotary sensitivity, and
# macro round-trip. Intended for T1 (192.168.1.110).
#
# Usage:  powershell -File keeb-smoke.ps1
# Assumes the service is already running on http://localhost:19494
# (build-pc skill leaves it that way after a deploy).

$ErrorActionPreference = "SilentlyContinue"
$base = "http://localhost:19494"
$pass = 0
$fail = 0
$warn = 0

function Got-Token {
    try { (Invoke-RestMethod -Uri "$base/pair" -UseBasicParsing).token }
    catch { $null }
}

function H($t) { @{ Authorization = "Bearer $t"; "Content-Type" = "application/json" } }

function Pass($msg) { Write-Host "  PASS $msg" -ForegroundColor Green; $script:pass++ }
function Fail($msg) { Write-Host "  FAIL $msg" -ForegroundColor Red; $script:fail++ }
function Warn($msg) { Write-Host "  WARN $msg" -ForegroundColor Yellow; $script:warn++ }

$token = Got-Token
if (-not $token) {
    Write-Host "FATAL: could not pair with service at $base" -ForegroundColor Red
    exit 1
}
Write-Host "Paired (token $($token.Substring(0,8))...)"
Write-Host ""

# 1. Connection check
Write-Host "[1] Device connectivity"
try {
    $state = Invoke-RestMethod -Uri "$base/keeb/state" -Headers (H $token) -UseBasicParsing
    if ($state.isConnected) {
        Pass "Keeb is connected (layout=$($state.layout), profile=$($state.profile))"
    } else {
        Warn "Keeb reported not connected — most subsequent tests will be offline-only"
    }
} catch {
    Fail "/keeb/state errored: $_"
}

# 2. Each layer returns key data
Write-Host ""
Write-Host "[2] Layer round-trip"
foreach ($l in 0..3) {
    try {
        $layer = Invoke-RestMethod -Uri "$base/keeb/layer/$l" -Headers (H $token) -UseBasicParsing
        $rowCount = $layer.keys.Count
        if ($rowCount -gt 0 -or -not $state.isConnected) {
            Pass "GET /keeb/layer/$l returned $rowCount rows"
        } else {
            Fail "GET /keeb/layer/$l returned 0 rows (expected device key map)"
        }
    } catch {
        Fail "GET /keeb/layer/$l errored: $_"
    }
}

# 3. Write a key on layer 0 and read it back
Write-Host ""
Write-Host "[3] Single-key write"
$writeBody = @{
    x = 2; y = 5
    func = "Macro1"
    mode = "MacroKey"
    input = 1
} | ConvertTo-Json
try {
    $resp = Invoke-RestMethod -Uri "$base/keeb/layer/0/key" -Method Post -Body $writeBody -Headers (H $token) -UseBasicParsing
    $written = $resp.keys[2][5]
    if ($written.function -eq "Macro1") {
        Pass "POST /keeb/layer/0/key Macro1 at (2,5) -> round-tripped"
    } else {
        Fail "Wrote Macro1 at (2,5) but readback shows '$($written.function)'"
    }
} catch {
    Fail "POST /keeb/layer/0/key errored: $_"
}

# 4. Reset the layer
Write-Host ""
Write-Host "[4] Layer reset"
try {
    Invoke-RestMethod -Uri "$base/keeb/layer/0/reset" -Method Post -Body "{}" -Headers (H $token) -UseBasicParsing | Out-Null
    Pass "POST /keeb/layer/0/reset accepted"
} catch {
    Fail "POST /keeb/layer/0/reset errored: $_"
}

# 5. Firmware lighting
Write-Host ""
Write-Host "[5] Firmware lighting writes"
foreach ($mode in @("Static","Breathe","Rainbow","Wave","Flow","PingPong")) {
    $body = @{
        animationMode = $mode
        speed = "Standard"
        direction = "LeftToRight"
        brightness = 70
        keyIndicator = $true
    } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$base/keeb/firmware/lighting" -Method Post -Body $body -Headers (H $token) -UseBasicParsing | Out-Null
        Pass "Effect $mode applied"
    } catch {
        Fail "Effect $mode errored: $_"
    }
    Start-Sleep -Milliseconds 250
}

# 6. Passive (key-reactive) lighting
Write-Host ""
Write-Host "[6] Passive lighting toggle"
$onBody = @{
    keyReactive = $true
    keyReactiveMask = $true
    keyReactiveMode = "Ripple"
    keyReactiveColor = @{ r = 200; g = 100; b = 50; a = 255 }
} | ConvertTo-Json -Depth 3
try {
    Invoke-RestMethod -Uri "$base/keeb/passive-lighting" -Method Post -Body $onBody -Headers (H $token) -UseBasicParsing | Out-Null
    Pass "Passive lighting ON (Ripple mode) accepted"
} catch {
    Fail "Passive lighting ON errored: $_"
}
Start-Sleep -Milliseconds 250
$offBody = @{ keyReactive = $false; keyReactiveMask = $false; keyReactiveMode = "Off"; keyReactiveColor = @{ r=0;g=0;b=0;a=255 } } | ConvertTo-Json -Depth 3
try {
    Invoke-RestMethod -Uri "$base/keeb/passive-lighting" -Method Post -Body $offBody -Headers (H $token) -UseBasicParsing | Out-Null
    Pass "Passive lighting OFF accepted"
} catch {
    Fail "Passive lighting OFF errored: $_"
}

# 7. Game mode
Write-Host ""
Write-Host "[7] Game mode toggle"
$gmOn = @{ altF4 = $true; altTab = $true; shiftTab = $true; windowsKey = $true } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "$base/keeb/game-mode" -Method Post -Body $gmOn -Headers (H $token) -UseBasicParsing | Out-Null
    Pass "Game mode ON (all lockouts) accepted"
} catch {
    Fail "Game mode ON errored: $_"
}
$gmOff = @{ altF4 = $false; altTab = $false; shiftTab = $false; windowsKey = $false } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "$base/keeb/game-mode" -Method Post -Body $gmOff -Headers (H $token) -UseBasicParsing | Out-Null
    Pass "Game mode OFF accepted"
} catch {
    Fail "Game mode OFF errored: $_"
}

# 8. Rotary sensitivity sweep
Write-Host ""
Write-Host "[8] Rotary sensitivity"
foreach ($s in @("Slow","Steady","Balanced","Fast","Turbo")) {
    $body = @{ sensitivity = $s } | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$base/keeb/rotary/sensitivity" -Method Post -Body $body -Headers (H $token) -UseBasicParsing | Out-Null
        Pass "Sensitivity $s accepted"
    } catch {
        Fail "Sensitivity $s errored: $_"
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
    $set = Invoke-RestMethod -Uri "$base/keeb/macro/0" -Method Post -Body $macroBody -Headers (H $token) -UseBasicParsing
    if ($set.macro.keys.Count -ge 4) {
        Pass "Macro write returned $($set.macro.keys.Count) keystrokes"
    } else {
        Warn "Macro write returned only $($set.macro.keys.Count) keystrokes (expected ≥4)"
    }
} catch {
    Fail "Macro write errored: $_"
}
try {
    $get = Invoke-RestMethod -Uri "$base/keeb/macro/0" -Headers (H $token) -UseBasicParsing
    if ($get.macro.keys.Count -ge 4) {
        Pass "Macro read returned $($get.macro.keys.Count) keystrokes"
    } else {
        Warn "Macro read returned $($get.macro.keys.Count) keystrokes"
    }
} catch {
    Fail "Macro read errored: $_"
}

# 10. Settings final check
Write-Host ""
Write-Host "[10] Settings shape"
try {
    $s = Invoke-RestMethod -Uri "$base/keeb/settings" -Headers (H $token) -UseBasicParsing
    if ($s.PSObject.Properties["animationMode"] -and $s.PSObject.Properties["keyReactive"]) {
        Pass "GET /keeb/settings returns split firmware + passive fields"
    } else {
        Fail "GET /keeb/settings missing expected fields"
    }
} catch {
    Fail "GET /keeb/settings errored: $_"
}

Write-Host ""
Write-Host "============================================"
Write-Host "Results: $pass passed, $fail failed, $warn warned"
Write-Host "============================================"
exit $fail
