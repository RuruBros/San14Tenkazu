param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Environment -BuildDirectory $BuildDirectory -DllSha256 'f946c9211da5a9efa3575b454e3d138c126e75532644a89115ce0ea5c6c4b94a' -ManifestSha256 '936f8de7d19da376a018715281151d481864279121756ef92b098da4e5dd4751'
