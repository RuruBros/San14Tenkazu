[CmdletBinding()]
param([switch]$VerifyComposition, [switch]$PackageOnly)

$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = Join-Path $taskRoot 'source'
$taskOutput = Join-Path $taskSource 'bin/Release/net8.0-windows'
$taskChecks = Join-Path $taskSource 'RenderChecks/bin/Release/net8.0-windows/San14Tenkazu.Tests.dll'
$taskArtifacts = Join-Path $taskSource 'RenderChecks/artifacts'
$taskPackageStage = $null
if ($PackageOnly) {
    $taskPackageStage = Join-Path $taskSource ('obj/packages/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N'))
    $taskOutput = Join-Path $taskPackageStage 'checked'
    $taskChecks = Join-Path $taskOutput 'San14Tenkazu.Tests.exe'
}

function Get-SourceState {
    $taskState = [ordered]@{}
    $taskFolders = [Collections.Generic.Stack[string]]::new()
    $taskCandidates = [Collections.Generic.List[IO.FileInfo]]::new()
    $taskFolders.Push($taskSource)
    while ($taskFolders.Count) {
        foreach ($taskEntry in Get-ChildItem -LiteralPath $taskFolders.Pop()) {
            if ($taskEntry.PSIsContainer) {
                if ($taskEntry.Name -notin @('bin', 'obj', 'artifacts', 'Tests', 'RenderChecks', 'Reverse')) { $taskFolders.Push($taskEntry.FullName) }
            }
            elseif ($taskEntry.Extension -in @('.cs', '.csproj', '.props', '.targets', '.txt', '.ico', '.cpp', '.h', '.hlsl', '.hlsli', '.zip', '.json', '.ps1')) { $taskCandidates.Add($taskEntry) }
        }
    }
    foreach ($taskFile in $taskCandidates | Sort-Object FullName) {
        $taskRelative = $taskFile.FullName.Substring($taskSource.Length + 1).Replace('\', '/')
        $taskState[$taskRelative] = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    }
    foreach ($taskDocument in @('README.txt', 'THIRD-PARTY-NOTICES.txt')) {
        $taskState['../' + $taskDocument] = (Get-FileHash -LiteralPath (Join-Path $taskRoot $taskDocument) -Algorithm SHA256).Hash
    }
    return $taskState
}

function Assert-SourceState($taskExpected) {
    $taskActual = Get-SourceState
    if (($taskExpected | ConvertTo-Json -Compress) -ne ($taskActual | ConvertTo-Json -Compress)) {
        throw 'Source changed during release verification. Rebuild this checkout before publishing.'
    }
}

function Invoke-Check([string[]]$taskArguments, [string]$taskLogName) {
    Write-Output "Checking $($taskArguments[0])..."
    if ($PackageOnly) { & $taskChecks @taskArguments *> (Join-Path $taskArtifacts $taskLogName) }
    else { & dotnet $taskChecks @taskArguments *> (Join-Path $taskArtifacts $taskLogName) }
    if ($LASTEXITCODE -ne 0) { throw "Release check failed; see source/RenderChecks/artifacts/$taskLogName" }
}

# Build and deploy only this checkout. A separate worktree's build can omit uncommitted UI fixes.
New-Item -ItemType Directory -Path $taskArtifacts -Force | Out-Null
Push-Location -LiteralPath $taskRoot
try {
    $taskState = Get-SourceState
    if ($PackageOnly) {
        Write-Output 'Building the Windows x64 self-contained release and its checks...'
        & dotnet publish (Join-Path $taskSource 'RenderChecks/San14Tenkazu.RenderChecks.csproj') -c Release -r win-x64 --self-contained true -o $taskOutput -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false *> (Join-Path $taskArtifacts 'publish-build.log')
    }
    else {
        & dotnet build (Join-Path $taskSource 'RenderChecks') --no-restore -c Release -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false *> (Join-Path $taskArtifacts 'publish-build.log')
    }
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed; see source/RenderChecks/artifacts/publish-build.log' }
    Assert-SourceState $taskState
    Invoke-Check @('--season-bundle-check', (Join-Path $taskSource 'Seasons')) 'publish-season-bundle.log'
    Invoke-Check @('--navigation', (Join-Path $taskArtifacts 'publish-navigation.jsonl')) 'publish-navigation.log'
    Invoke-Check @('--panes') 'publish-panes.log'
    Invoke-Check @('--independence', (Join-Path $taskArtifacts 'publish-independence')) 'publish-independence.log'
    Invoke-Check @('--pane-history', (Join-Path $taskArtifacts 'publish-pane-history')) 'publish-pane-history.log'
    Invoke-Check @('--rules', (Join-Path $taskArtifacts 'publish-rules')) 'publish-rules.log'
    Invoke-Check @('--policy-rules', (Join-Path $taskArtifacts 'publish-policy-rules')) 'publish-policy-rules.log'
    Invoke-Check @('--office-rules', (Join-Path $taskArtifacts 'publish-office-rules')) 'publish-office-rules.log'
    Invoke-Check @('--doctrine-rules', (Join-Path $taskArtifacts 'publish-doctrine-rules')) 'publish-doctrine-rules.log'
    Invoke-Check @('--rank-rules', (Join-Path $taskArtifacts 'publish-rank-rules')) 'publish-rank-rules.log'
    Invoke-Check @('--facilities', (Join-Path $taskArtifacts 'publish-facilities.jsonl')) 'publish-facilities.log'
    Invoke-Check @('--military-points', (Join-Path $taskArtifacts 'publish-military-points')) 'publish-military-points.log'
    Invoke-Check @('--force-policies', (Join-Path $taskArtifacts 'publish-force-policies')) 'publish-force-policies.log'
    Invoke-Check @('--pending-inputs', (Join-Path $taskArtifacts 'publish-pending-inputs')) 'publish-pending-inputs.log'
    Invoke-Check @('--unit-conditions', (Join-Path $taskArtifacts 'publish-unit-conditions')) 'publish-unit-conditions.log'
    Invoke-Check @('--officer-merit', (Join-Path $taskArtifacts 'publish-officer-merit')) 'publish-officer-merit.log'
    Invoke-Check @('--officer-revival', (Join-Path $taskArtifacts 'publish-officer-revival')) 'publish-officer-revival.log'
    Invoke-Check @('--officer-status', (Join-Path $taskArtifacts 'publish-officer-status')) 'publish-officer-status.log'
    Invoke-Check @('--registered', (Join-Path $taskArtifacts 'publish-registered')) 'publish-registered.log'
    Invoke-Check @('--tooltips', (Join-Path $taskArtifacts ('publish-tooltips-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')))) 'publish-tooltips.log'
    Invoke-Check @('--choices', (Join-Path $taskArtifacts ('publish-choices-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')))) 'publish-choices.log'
    Invoke-Check @('--calendar', (Join-Path $taskArtifacts ('publish-calendar-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')))) 'publish-calendar.log'
    Invoke-Check @('--gpu-lifecycle') 'publish-gpu-lifecycle.log'
    Invoke-Check @('--memory-resources') 'publish-memory-resources.log'
    Invoke-Check @('--performance', (Join-Path $taskArtifacts 'publish-performance')) 'publish-performance.log'
    Invoke-Check @('--coast') 'publish-coast.log'
    Invoke-Check @('--unit-interpolation', (Join-Path $taskArtifacts 'publish-unit-interpolation')) 'publish-unit-interpolation.log'
    Invoke-Check @('--season-editor', (Join-Path $taskArtifacts ('publish-season-editor-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')))) 'publish-season-editor.log'
    if ($VerifyComposition) {
        Invoke-Check @('--window-move', '--expect-responsive', (Join-Path $taskArtifacts 'publish-window-move')) 'publish-window-move.log'
        Invoke-Check @('--drag', (Join-Path $taskArtifacts 'publish-drag')) 'publish-drag.log'
        Invoke-Check @('--swipe', (Join-Path $taskArtifacts 'publish-swipe')) 'publish-swipe.log'
        Invoke-Check @('--zoom', (Join-Path $taskArtifacts 'publish-zoom')) 'publish-zoom.log'
        Invoke-Check @('--motion', (Join-Path $taskArtifacts 'publish-motion')) 'publish-motion.log'
    }
    Assert-SourceState $taskState

    if ($PackageOnly) {
        & (Join-Path $taskSource 'Release.Package.ps1') -TaskRoot $taskRoot -TaskStage $taskPackageStage -TaskCheckedOutput $taskOutput -TaskSourceState $taskState -VerifyComposition:$VerifyComposition
        Assert-SourceState $taskState
        return
    }

    $taskFiles = @(Get-ChildItem -LiteralPath $taskOutput -Recurse -File | Where-Object { $_.Extension -in @('.exe', '.dll', '.pdb', '.json') })
    $taskRows = @()
    foreach ($taskFile in $taskFiles) {
        $taskRelative = $taskFile.FullName.Substring($taskOutput.Length + 1)
        $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
        if (-not $taskDestination.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid release destination' }
        $taskRows += [PSCustomObject]@{ relative = $taskRelative; source = $taskFile.FullName; destination = $taskDestination;
            sha256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash }
    }
    if (-not ($taskRows.relative -contains 'San14Tenkazu.dll') -or -not ($taskRows.relative -contains 'San14Tenkazu.Core.dll')) { throw 'Incomplete release output' }
    $taskBackup = [IO.Path]::GetFullPath((Join-Path $taskSource ('obj/deploy-backup/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))))
    if (-not $taskBackup.StartsWith((Join-Path $taskSource 'obj/deploy-backup') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup destination' }
    $taskChanged = @()
    try {
        foreach ($taskRow in $taskRows) {
            $taskBefore = if (Test-Path -LiteralPath $taskRow.destination) { (Get-FileHash -LiteralPath $taskRow.destination -Algorithm SHA256).Hash } else { $null }
            if ($taskBefore -eq $taskRow.sha256) { continue }
            $taskSaved = if ($taskBefore) { [IO.Path]::GetFullPath((Join-Path $taskBackup $taskRow.relative)) } else { $null }
            if ($taskSaved) {
                if (-not $taskSaved.StartsWith($taskBackup + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup file' }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskSaved)) | Out-Null
                Copy-Item -LiteralPath $taskRow.destination -Destination $taskSaved
            }
            $taskChanged += [PSCustomObject]@{ destination = $taskRow.destination; backup = $taskSaved }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskRow.destination)) | Out-Null
            try { Copy-Item -LiteralPath $taskRow.source -Destination $taskRow.destination -Force }
            catch [IO.IOException] {
                # A running .NET process can retain its old image while the next launch uses the new file.
                $taskLoaded = [IO.Path]::GetFullPath((Join-Path $taskBackup ('loaded/' + $taskRow.relative)))
                if (-not $taskLoaded.StartsWith($taskBackup + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid loaded-image backup' }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskLoaded)) | Out-Null
                Move-Item -LiteralPath $taskRow.destination -Destination $taskLoaded
                Copy-Item -LiteralPath $taskRow.source -Destination $taskRow.destination
            }
        }
        foreach ($taskRow in $taskRows) {
            if ((Get-FileHash -LiteralPath $taskRow.destination -Algorithm SHA256).Hash -ne $taskRow.sha256) { throw "Deployed hash mismatch: $($taskRow.relative)" }
        }
        Assert-SourceState $taskState
        $taskManifest = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); checkout = $taskRoot; sources = $taskState;
            files = @($taskRows | Select-Object relative, sha256); compositionChecked = $VerifyComposition.IsPresent; backup = $taskBackup }
        $taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'San14Tenkazu.release.json') -Encoding utf8
    }
    catch {
        foreach ($taskEntry in $taskChanged) {
            if ($taskEntry.backup) { Copy-Item -LiteralPath $taskEntry.backup -Destination $taskEntry.destination -Force }
            elseif (Test-Path -LiteralPath $taskEntry.destination) { Remove-Item -LiteralPath $taskEntry.destination }
        }
        throw
    }
    Write-Output "Published $($taskRows.Count) verified files from $taskRoot; changed $($taskChanged.Count)."
    Write-Output 'Running instances retain the previous build; restart the editor to use this release.'
}
finally { Pop-Location }
