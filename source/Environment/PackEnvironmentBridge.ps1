param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Environment -BuildDirectory $BuildDirectory -DllSha256 'f1fda5b0513ba05d0bf5346b323b7c897a5eaaabf3bb15cbd5b3d099b3b42399' -ManifestSha256 '771185e52ddcdcdcb767a805fe139a9129a613f5223e2622d469087f40804579'
