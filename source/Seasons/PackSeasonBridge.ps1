param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Season -BuildDirectory $BuildDirectory -DllSha256 '42d2ed9d2b0640aedb45a92d64f62d1a081262de38e2c9b25b0859bb0d3214f0' -ManifestSha256 'bebbcd2ef29115e68c542a1b4144ce4276a55e4b6884b1e2febfbf8280d5b4d3'
