[CmdletBinding()]
param([switch]$VerifyComposition)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Publish.ps1') -PackageOnly -VerifyComposition:$VerifyComposition

