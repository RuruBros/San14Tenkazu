param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Season -BuildDirectory $BuildDirectory -DllSha256 '375811da8a8bc163758f85cb56eef8128f781b06389df09ca9889da33b90ba8a' -ManifestSha256 '88e05ec4c4833cc33ed8157bd58298552dc8a01e29d4d263ea8353658d37e098'
