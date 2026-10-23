param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Environment -BuildDirectory $BuildDirectory -DllSha256 '8131de363284def4984a429c96462fcd190f7b13555cb83092fd5a40726a4258' -ManifestSha256 'cff2d7f01e0b41b6b8fad3f54ebb926d0fd4e8be4c847e2df134fdf23daaeb20'
