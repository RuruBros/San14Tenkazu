# Startup frame capture only. Does not close a running game or apply seasons.
param()
$ErrorActionPreference = 'Stop'
$taskGameExe = 'C:\Program Files (x86)\Steam\steamapps\common\Romance_of_the_Three_Kingdoms_14\SAN14CE_KO.exe'
$taskTool = Join-Path $PSScriptRoot '.cache\tools\renderdoc-1.46\portable\RenderDoc_1.46_64'
if (Get-Process -Name SAN14CE_KO -ErrorAction SilentlyContinue) {
    throw 'Close SAN14 normally first. Late injection into a running D3D11 game is not used.'
}
$taskExpected = @{
    $taskGameExe = '87ed4e46590e0939c9efcaef14fa0f892917604dfda86e0a4be319fccffa2d6a'
    (Join-Path $taskTool 'renderdoccmd.exe') = '400cb013ea52b9baa818d69dc0a6341776d2a9560edad5bcc6e200fe8c03806d'
    (Join-Path $taskTool 'renderdoc.dll') = '809da38e3867d9fd09cc5c30dd5310500dee75e999166d7a14ad1ef6e9eca65d'
}
foreach ($taskEntry in $taskExpected.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath $taskEntry.Key -Algorithm SHA256).Hash -ne $taskEntry.Value) {
        throw "Capture input hash differs: $($taskEntry.Key)"
    }
}
$taskOutput = Join-Path $PSScriptRoot ('.cache\season-game-captures\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop | Out-Null
$taskTemplate = Join-Path $taskOutput 'san14'
$taskManifest = [ordered]@{
    status = 'Prepared for startup capture; no frame captured yet'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    toolVersion = 'RenderDoc 1.46 x64'
    inputHashes = $taskExpected
    captureTemplate = $taskTemplate
    trigger = 'User presses F12 once on the intended map frame'
    expectedCaptureCount = 1
    startupOnly = $true
    captureCallstacksOnlyActions = $true
    gameStoppedByScript = $false
    lateInjection = $false
    retiredCommandProbeRetried = $false
    seasonApplied = $false
    productionReady = $false
}
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'launch.json') -Encoding UTF8
# Keep the helper until the game exits. Arguments contain only verified fixed paths.
$taskArguments = @('capture','-w','--opt-capture-callstacks','--opt-capture-callstacks-only-actions',
    '-d',('"' + (Split-Path -Parent $taskGameExe) + '"'),
    '-c',('"' + $taskTemplate + '"'),('"' + $taskGameExe + '"'))
$taskLauncher = Start-Process -FilePath (Join-Path $taskTool 'renderdoccmd.exe') -ArgumentList $taskArguments `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutput 'launcher.stdout.log') `
    -RedirectStandardError (Join-Path $taskOutput 'launcher.stderr.log')
$taskManifest['launcherPid'] = $taskLauncher.Id
$taskManifest['status'] = 'Startup launcher dispatched; game startup and capture unverified'
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'launch.json') -Encoding UTF8
[pscustomobject]@{ launcherPid=$taskLauncher.Id; output=$taskOutput; frameCaptured=$false } | ConvertTo-Json
