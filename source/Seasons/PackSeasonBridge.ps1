param([Parameter(Mandatory=$true)][string]$BuildDirectory,[switch]$November2026)
$ErrorActionPreference='Stop'
if($November2026){
    $taskDll='df675e050e008e46c1b4192c02ff66f3f19258b3e5aea744477384a4517c2fd5'
    $taskManifest='9e572a98cc5880db449f44652824f73ac8e2784f532fee7766dfe77fbb2fdefb'
}else{
    $taskDll='62e964b1acc70188374da754a6bba8ea270e5f2643be742042adefa7e878535b'
    $taskManifest='3a82dfdca149442bc7ee4082f452bf452172b1df7b8fcb9a64346ebb6f56c0aa'
}
& (Join-Path $PSScriptRoot '..\BuildProfiles\PackNative.ps1') -Kind Season -BuildDirectory $BuildDirectory -DllSha256 $taskDll -ManifestSha256 $taskManifest -November2026:$November2026
