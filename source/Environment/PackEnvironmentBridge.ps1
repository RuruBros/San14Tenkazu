param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Environment -BuildDirectory $BuildDirectory -DllSha256 'aae339e0a60b8e8ea2049dad880835ab1a83dcc50a7d2314dab22a4b2113c65d' -ManifestSha256 '4ca59103fd0bb88944cc6e2a03dad40d8bb30b2d8921197906f258963540d6bc'
