[CmdletBinding()]
param([switch]$VerifyComposition,[switch]$PublicRelease)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Publish.ps1') -PackageOnly -VerifyComposition:$VerifyComposition -PublicRelease:$PublicRelease

