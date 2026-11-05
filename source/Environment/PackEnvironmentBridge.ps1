param([Parameter(Mandatory=$true)][string]$BuildDirectory,[switch]$November2026)
$ErrorActionPreference='Stop'
if($November2026){
    $taskDll='b02d7abb87ea7b334d9f9cae5f8bc3a14292d6b6cd33f6b5835156fb6f194d89'
    $taskManifest='73f2ead2c791b78c464f2cec53b0756c19a8f49e7b31ae3a097c3d3cc46e3a1d'
}else{
    $taskDll='cc6ef30a41187b055e25f8577dc660063cf4c2ca5370fb28a05f83512c104565'
    $taskManifest='591153077cdb337ae99269ae0c4ba78a038ce71b7f686080e7009c090cb3e7b5'
}
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Environment -BuildDirectory $BuildDirectory -DllSha256 $taskDll -ManifestSha256 $taskManifest -November2026:$November2026
