param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Season -BuildDirectory $BuildDirectory -DllSha256 'a221f1a3b5d99266e4a04fd72a04dbc385f0b5d34697e9f42fc7adc86a2ac113' -ManifestSha256 'c8f3f28b2f7c48eac35d8f8bf561e8ca1b7984104f0128c8d0cea2c6c28c1394'
