[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
function Get-ProfileFileHash([string]$taskPath) {
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($taskHasher.ComputeHash([IO.File]::ReadAllBytes($taskPath))).Replace('-', '').ToLowerInvariant() }
    finally { $taskHasher.Dispose() }
}
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskPrevious = @('Core/BuildProfiles/GameBuildFacts.g.cs', 'Seasons/Native/GameBuildFacts.g.h', 'Environment/Native/GameBuildFacts.g.h', 'Seasons/Native/FrameSignatures.h', 'Seasons/Native/PrepareSignature.h', 'Environment/Native/EnvironmentSignatures.h')
$taskNovember = @('Core/BuildProfiles/GameNativeFactsNovember2026.g.cs') +
    @($taskPrevious | Select-Object -Skip 1 | ForEach-Object { 'BuildProfiles/November2026/' + $_ }) +
    @('BuildProfiles/November2026/Environment/Native/EnvironmentNoticeSignatures.g.h')
function Assert-Profile([string]$taskManifest, [string]$taskLockName, [string[]]$taskExpected) {
$taskProfile = Get-Content -LiteralPath (Join-Path $PSScriptRoot $taskManifest) -Raw | ConvertFrom-Json
$taskLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot $taskLockName) -Raw | ConvertFrom-Json
$taskHash = Get-ProfileFileHash (Join-Path $PSScriptRoot $taskManifest)
if ($taskProfile.schemaVersion -ne 1 -or $taskLock.schemaVersion -ne 1 -or $taskLock.profileSha256 -cne $taskHash) { throw 'Build profile definition changed; regenerate and review its outputs.' }
$taskActual = @($taskLock.files.PSObject.Properties.Name)
if ($taskActual.Count -ne $taskExpected.Count -or @($taskActual | Where-Object { $_ -cnotin $taskExpected }).Count -ne 0) { throw 'Generated build profile inventory differs.' }
foreach ($taskProperty in $taskLock.files.PSObject.Properties) {
    if ((Get-ProfileFileHash (Join-Path $taskRoot $taskProperty.Name)) -cne $taskProperty.Value) { throw ('Stale generated build profile file: ' + $taskProperty.Name) }
}
foreach ($taskResource in $taskProfile.resources) {
    if ($taskResource.path -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($taskResource.path)) { throw 'Build profile resource escaped source directory.' }
    if ((Get-ProfileFileHash (Join-Path $taskRoot $taskResource.path)) -cne $taskResource.sha256) { throw ('Build profile resource differs: ' + $taskResource.name) }
}
Write-Output ('PASS generated build profile ' + $taskProfile.id)
}
Assert-Profile 'current.json' 'generated.lock.json' $taskPrevious
Assert-Profile 'november2026.json' 'november2026.lock.json' $taskNovember
