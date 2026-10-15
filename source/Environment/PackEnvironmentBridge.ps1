param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Environment -BuildDirectory $BuildDirectory -DllSha256 '32cb74defb2620ce1ad0bae22ca80e00aa4f9bc77e2a3e86815b56a368da6aba' -ManifestSha256 '332925a832216994c1b67c6cddd28418f19a7422bc237af8fe1c6a88eb7cc9aa'
