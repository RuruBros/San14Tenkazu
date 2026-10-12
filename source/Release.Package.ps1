# Called by Publish.ps1 after the self-contained release passes all required checks.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$TaskStage,
    [Parameter(Mandatory)][string]$TaskCheckedOutput,
    [Parameter(Mandatory)][Collections.IDictionary]$TaskSourceState,
    [switch]$VerifyComposition,
    [switch]$DeveloperMenu
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskPayload = Join-Path $TaskStage 'San14Tenkazu'
$taskLog = Join-Path $TaskStage 'package-publish.log'
$taskBuildFlavor = @(('-p:LocalDeveloperPackage=' + $DeveloperMenu.IsPresent.ToString().ToLowerInvariant()), '-p:DeveloperPackageBuild=true')

# Publish the app from the same build that was tested, without recompiling it.
& dotnet publish (Join-Path $TaskRoot 'source/San14Tenkazu.csproj') -c Release -r win-x64 --self-contained true --no-build --no-restore -o $taskPayload -p:DebugType=None -p:DebugSymbols=false @taskBuildFlavor *> $taskLog
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed; see $taskLog" }
Assert-SourceState $TaskSourceState
foreach ($taskAssembly in @('San14Tenkazu.dll', 'San14Tenkazu.Core.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskPayload $taskAssembly)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $TaskCheckedOutput $taskAssembly)).Hash) {
        throw "Package assembly differs from the checked build: $taskAssembly"
    }
}

$taskRuntime = (Get-Content -LiteralPath (Join-Path $taskPayload 'San14Tenkazu.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
if ($taskRuntime.framework -or $taskRuntime.frameworks -or @($taskRuntime.includedFrameworks).Count -ne 2 -or
    @($taskRuntime.includedFrameworks.name) -notcontains 'Microsoft.NETCore.App' -or
    @($taskRuntime.includedFrameworks.name) -notcontains 'Microsoft.WindowsDesktop.App') {
    throw 'The portable app must include both .NET and Windows Desktop runtimes'
}
foreach ($taskRequired in @('San14Tenkazu.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskPayload $taskRequired) -PathType Leaf)) { throw "Missing portable dependency: $taskRequired" }
}
if (Get-ChildItem -LiteralPath $taskPayload -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like 'San14Tenkazu.Tests.*' }) {
    throw 'Debug symbols or check binaries leaked into the portable payload'
}
foreach ($taskDocument in @('README.txt', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $TaskRoot $taskDocument) -Destination (Join-Path $taskPayload $taskDocument)
}

# Runtime-pack licenses are not copied by dotnet publish. Resolve their exact
# versions from the app and their cache roots from restore's assets file.
$taskAssets = Get-Content -LiteralPath (Join-Path $TaskRoot 'source/obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($taskFramework in $taskRuntime.includedFrameworks) {
    $taskRuntimeId = $taskFramework.name.ToLowerInvariant() + '.runtime.win-x64'
    $taskRuntimePackage = @($taskAssets.packageFolders.PSObject.Properties.Name | ForEach-Object {
        Join-Path $_ ($taskRuntimeId + '/' + $taskFramework.version)
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Container }) | Select-Object -First 1
    if (-not $taskRuntimePackage) { throw "Runtime-pack license source is missing: $taskRuntimeId" }
    $taskLicenses = @(Get-ChildItem -LiteralPath $taskRuntimePackage -File | Where-Object { $_.Name -in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') })
    if (-not ($taskLicenses | Where-Object { $_.Name -in @('LICENSE', 'LICENSE.TXT') })) { throw "Runtime license is missing: $taskRuntimeId" }
    if ($taskFramework.name -eq 'Microsoft.NETCore.App' -and -not ($taskLicenses | Where-Object { $_.Name -eq 'THIRD-PARTY-NOTICES.TXT' })) {
        throw '.NET runtime third-party notices are missing'
    }
    $taskLicenseOutput = Join-Path $taskPayload ('licenses/' + $taskRuntimeId)
    [IO.Directory]::CreateDirectory($taskLicenseOutput) | Out-Null
    foreach ($taskLicense in $taskLicenses) { Copy-Item -LiteralPath $taskLicense.FullName -Destination (Join-Path $taskLicenseOutput $taskLicense.Name) }
}

$taskVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskPayload 'San14Tenkazu.exe')).FileVersion
if ($taskVersion -notmatch '^\d+(\.\d+){1,3}$') { throw "Invalid package version: $taskVersion" }
$taskName = 'San14Tenkazu-' + $taskVersion + '-win-x64-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
if ($DeveloperMenu) { $taskName += '-local-developer' }
$taskRows = @(foreach ($taskFile in Get-ChildItem -LiteralPath $taskPayload -Recurse -File | Sort-Object FullName) {
    [PSCustomObject]@{ relative = $taskFile.FullName.Substring($taskPayload.Length + 1).Replace('\', '/');
        bytes = $taskFile.Length; sha256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash }
})
$taskManifestPath = Join-Path $taskPayload 'San14Tenkazu.package.json'
[ordered]@{ sources = $TaskSourceState; compositionChecked = $VerifyComposition.IsPresent } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $TaskStage 'package-source-verification.json') -Encoding utf8
[ordered]@{ formatVersion = 1; product = 'San14Tenkazu'; version = $taskVersion; runtimeIdentifier = 'win-x64';
    selfContained = $true; developerMenu = $DeveloperMenu.IsPresent; publicRelease = (-not $DeveloperMenu.IsPresent); utc = [DateTime]::UtcNow.ToString('o'); runtimes = @($taskRuntime.includedFrameworks);
    files = $taskRows } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
$taskExpected = @{}
foreach ($taskRow in $taskRows) { $taskExpected['San14Tenkazu/' + $taskRow.relative] = $taskRow }
$taskExpected['San14Tenkazu/San14Tenkazu.package.json'] = [PSCustomObject]@{ bytes = (Get-Item -LiteralPath $taskManifestPath).Length;
    sha256 = (Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash }

$taskZipPath = Join-Path $TaskStage ($taskName + '.zip')
Write-Output "Compressing and verifying $($taskExpected.Count) portable files..."
# .NET Framework's CreateFromDirectory writes backslashes on Windows. Use explicit
# ZIP entry names so PowerShell 5.1 and 7 both produce standard portable paths.
$taskZipBuilder = [IO.Compression.ZipFile]::Open($taskZipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskEntryName in $taskExpected.Keys | Sort-Object) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZipBuilder, (Join-Path $TaskStage $taskEntryName), $taskEntryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $taskZipBuilder.Dispose() }
$taskZip = [IO.Compression.ZipFile]::OpenRead($taskZipPath)
try {
    $taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($taskEntry in $taskZip.Entries) {
        if (-not $taskExpected.ContainsKey($taskEntry.FullName) -or -not $taskSeen.Add($taskEntry.FullName)) { throw "Unexpected ZIP entry: $($taskEntry.FullName)" }
        $taskStream = $taskEntry.Open()
        $taskHasher = [Security.Cryptography.SHA256]::Create()
        try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-', '') }
        finally { $taskHasher.Dispose(); $taskStream.Dispose() }
        if ($taskEntry.Length -ne $taskExpected[$taskEntry.FullName].bytes -or $taskHash -ne $taskExpected[$taskEntry.FullName].sha256) {
            throw "ZIP content mismatch: $($taskEntry.FullName)"
        }
    }
    if ($taskSeen.Count -ne $taskExpected.Count) { throw 'The ZIP is missing payload files' }
}
finally { $taskZip.Dispose() }

# Run a check harness beside the extracted app, using only the runtime actually in the ZIP.
# The editor's Main is not invoked: no game attachment or user profile writes.
$taskExtracted = Join-Path $TaskStage 'extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($taskZipPath, $taskExtracted)
$taskSmokeRoot = Join-Path $taskExtracted 'San14Tenkazu'
foreach ($taskCheckFile in @('San14Tenkazu.Tests.exe', 'San14Tenkazu.Tests.dll', 'San14Tenkazu.Tests.deps.json', 'San14Tenkazu.Tests.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $TaskCheckedOutput $taskCheckFile) -Destination (Join-Path $taskSmokeRoot $taskCheckFile)
}
$taskStart = [Diagnostics.ProcessStartInfo]::new()
$taskStart.FileName = Join-Path $taskSmokeRoot 'San14Tenkazu.Tests.exe'
$taskStart.Arguments = '--package-smoke "' + (Join-Path $TaskStage 'package-smoke.json') + '"'
$taskStart.WorkingDirectory = $taskSmokeRoot
$taskStart.UseShellExecute = $false
$taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardOutput = $true
$taskStart.RedirectStandardError = $true
$taskStart.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $TaskStage 'no-installed-dotnet'
$taskStart.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $TaskStage 'no-installed-dotnet'
$taskStart.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$taskStart.EnvironmentVariables['COREHOST_TRACE'] = '1'
$taskStart.EnvironmentVariables['COREHOST_TRACEFILE'] = Join-Path $TaskStage 'package-host.log'
$taskSmoke = [Diagnostics.Process]::Start($taskStart)
try {
    $taskStdout = $taskSmoke.StandardOutput.ReadToEndAsync()
    $taskStderr = $taskSmoke.StandardError.ReadToEndAsync()
    if (-not $taskSmoke.WaitForExit(60000)) { $taskSmoke.Kill(); throw 'Portable runtime smoke check timed out' }
    ($taskStdout.GetAwaiter().GetResult() + $taskStderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath (Join-Path $TaskStage 'package-smoke.log') -Encoding utf8
    if ($taskSmoke.ExitCode -ne 0) { throw "Portable runtime smoke check failed; see $TaskStage/package-smoke.log" }
}
finally { $taskSmoke.Dispose() }

Assert-SourceState $TaskSourceState
$taskDist = Join-Path $TaskRoot 'dist'
[IO.Directory]::CreateDirectory($taskDist) | Out-Null
$taskFinalZip = Join-Path $taskDist ($taskName + '.zip')
$taskFinalHash = $taskFinalZip + '.sha256'
if ((Test-Path -LiteralPath $taskFinalZip) -or (Test-Path -LiteralPath $taskFinalHash)) { throw 'Package destination already exists' }
$taskZipHash = (Get-FileHash -LiteralPath $taskZipPath -Algorithm SHA256).Hash
try {
    Copy-Item -LiteralPath $taskZipPath -Destination $taskFinalZip
    if ((Get-FileHash -LiteralPath $taskFinalZip -Algorithm SHA256).Hash -ne $taskZipHash) { throw 'Final package hash mismatch' }
    Assert-SourceState $TaskSourceState
    ($taskZipHash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($taskFinalZip)) | Set-Content -LiteralPath $taskFinalHash -Encoding ascii
}
catch {
    foreach ($taskIncomplete in @($taskFinalZip, $taskFinalHash)) {
        if (Test-Path -LiteralPath $taskIncomplete) { Remove-Item -LiteralPath $taskIncomplete }
    }
    throw
}
Write-Output "Packaged $($taskExpected.Count) verified files: $taskFinalZip"
Write-Output "SHA256: $taskZipHash"
Write-Output "Verification logs: $TaskStage"

