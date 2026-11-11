[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('preview','run')][string]$Mode,
    [Parameter(Mandatory=$true)][int]$GameProcessId,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$ExpectedManifest,
    [string[]]$Keys,
    [int]$SeedProvokeCommander
)
$ErrorActionPreference = 'Stop'
$taskRunner = Join-Path $PSScriptRoot 'bin/Release/net8.0/San14Tenkazu.Tests.dll'
if (!(Test-Path -LiteralPath $taskRunner)) { throw 'Build the developer checks project with LocalDeveloperPackage=true and DeveloperPackageBuild=true first.' }
if ($Mode -eq 'run' -and !$ExpectedManifest) { throw 'A reviewed preview manifest is required before any write.' }
$taskArguments = @($taskRunner, $Mode, "--pid=$GameProcessId", "--output=$Output")
if ($ExpectedManifest) { $taskArguments += "--expected-manifest=$ExpectedManifest" }
if ($Keys) { $taskArguments += ('--keys=' + ($Keys -join ',')) }
if ($SeedProvokeCommander) { $taskArguments += "--seed-provoke-commander=$SeedProvokeCommander" }
& dotnet @taskArguments
exit $LASTEXITCODE
