param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Season -BuildDirectory $BuildDirectory -DllSha256 '3648134e0448ce366edf39afdb9b5cf2c6b58a2121e2624904bb405562c0832c' -ManifestSha256 '961022452f45c718e0aa0eff16e6e9d18d70b686c20e00170419f41141b5e07b'
