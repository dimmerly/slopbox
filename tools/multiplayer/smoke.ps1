param([string]$EditorExecutable = 'X:\External\prowl\Build\Editor\Debug\net10.0\Prowl.Editor.exe')

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$cli = Join-Path $projectRoot 'Library/prowl.cmd'
$probe = Join-Path $projectRoot 'Assets/code/MultiplayerProbe.cs'
$clients = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$started = $false
$copied = $false
$location = Get-Location

function Invoke-Prowl([string[]]$Arguments) {
    $json = & $cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Prowl failed: $Arguments" }
    return ($json | ConvertFrom-Json)
}

function Evaluate([string]$Code) {
    $json = $Code | & $cli eval -
    if ($LASTEXITCODE -ne 0) { throw "Prowl eval failed" }
    return ($json | ConvertFrom-Json)
}

function Launch-Client([string]$Name, [int]$Index) {
    $log = Join-Path $env:TEMP "slopbox-$Name.log"
    $errors = Join-Path $env:TEMP "slopbox-$Name-errors.log"
    $arguments = @('--play-instance', $projectRoot, '--join', "127.0.0.1:$script:port", '--name', $Name, '--instance', $Index, '--headless')
    $process = Start-Process -FilePath $EditorExecutable -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError $errors -PassThru
    $clients.Add($process)
}

function Await-Result([string]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $state = Evaluate 'return new { result = Slopbox.MultiplayerProbe.Result, checkpoints = Slopbox.MultiplayerProbe.Checkpoints };'
        if ($state.result -like 'FAIL:*') { throw $state.result }
        if ($state.result -eq $Expected) { return $state }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Expected. Last state: $($state | ConvertTo-Json -Compress)"
}

try {
    Set-Location $projectRoot
    $status = Invoke-Prowl -Arguments @('status')
    if ($status.isPlaying) { throw 'Stop play mode before running the multiplayer smoke test.' }
    if (Test-Path -LiteralPath $probe) { throw "Temporary probe already exists: $probe" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MultiplayerProbe.cs') -Destination $probe
    $copied = $true
    $compile = Invoke-Prowl -Arguments @('command', 'compile')
    if (!$compile.ok) { throw ($compile | ConvertTo-Json -Depth 8) }
    Invoke-Prowl -Arguments @('command', 'play', '--action', 'start') | Out-Null
    $started = $true
    $hostState = Evaluate @'
Slopbox.MultiplayerProbe.Result = "waiting";
Slopbox.MultiplayerProbe.Checkpoints = 0;
var go = new GameObject("Multiplayer Probe");
go.AddComponent<Slopbox.MultiplayerProbe>();
Scene.Current.Add(go);
go.Network.Mode = Prowl.Runtime.Networking.NetworkMode.Object;
Slopbox.GameManager.Instance.Host("SmokeHost");
return new { port = Prowl.Runtime.Networking.Networking.LocalPort };
'@
    $script:port = $hostState.port
    if (!$script:port) { throw 'Host did not start.' }
    Launch-Client 'SmokeClient' 71
    $main = Await-Result 'pass'
    Write-Output "Gameplay passed ($($main.checkpoints) checkpoints)."
    Invoke-Prowl -Arguments @('command', 'input', '--action', 'key', '--keys', 'W,ControlLeft', '--seconds', '2') | Out-Null
    Await-Result 'animation-pass' | Out-Null
    Invoke-Prowl -Arguments @('command', 'input', '--action', 'key', '--keys', 'Space', '--seconds', '0.1') | Out-Null
    Await-Result 'jump-pass' | Out-Null
    Write-Output 'Remote locomotion, crouch and jump animation passed.'
    Launch-Client 'LateClient' 72
    $late = Await-Result 'late-pass'
    Write-Output "Late join passed ($($late.checkpoints) checkpoints total)."
    $world = Evaluate 'return new { players = Scene.Current.FindObjectsOfType<Slopbox.Player>().Length, hostUndo = Slopbox.GameManager.Instance.LocalPlayer.Undo.Count };'
    if ($world.players -ne 3 -or $world.hostUndo -ne 0) { throw "Player count or undo isolation failed: $($world | ConvertTo-Json)" }

    # Disconnect one real client while the other stays connected.
    $clients[1].Kill()
    $clients[1].WaitForExit(10000) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $world = Evaluate 'return new { players = Scene.Current.FindObjectsOfType<Slopbox.Player>().Length };'
        if ($world.players -eq 2) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($world.players -ne 2) { throw 'Disconnected player was not removed.' }
    Write-Output 'Disconnect cleanup and undo isolation passed.'
}
finally {
    if ($started) { Invoke-Prowl -Arguments @('command', 'play', '--action', 'stop') | Out-Null }
    foreach ($client in $clients) {
        if (!$client.HasExited -and !$client.WaitForExit(5000)) { $client.Kill() }
        $client.Dispose()
    }
    if ($copied) {
        Remove-Item -LiteralPath $probe
        if (Test-Path -LiteralPath "$probe.meta") { Remove-Item -LiteralPath "$probe.meta" }
        $compile = Invoke-Prowl -Arguments @('command', 'compile')
        if (!$compile.ok) { throw ($compile | ConvertTo-Json -Depth 8) }
    }
    Set-Location $location
}
