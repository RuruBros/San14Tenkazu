param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Season -BuildDirectory $BuildDirectory -DllSha256 'bacb74f5a5ef38772837918485631102745f4678e5aa2c794572a3ad7cf60020' -ManifestSha256 '56279b7702a98b1759c57145d57aa11182559b712e1d7eb8a3ef52ae36aaca00'
