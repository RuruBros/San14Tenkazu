param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$taskSourceFiles=@('DrawObserver.h','DrawObserver.cpp','DrawObserverChecks.cpp','DrawIndexedObserver.cpp','DrawIndexedObserverChecks.cpp','DrawInstancedObserver.cpp','DrawInstancedObserverChecks.cpp','ColorInstancedObserver.cpp','ColorInstancedObserverChecks.cpp','TextureReadObserver.h','TextureReadObserver.cpp','TextureReadObserverChecks.cpp','TerrainTrial.h','TerrainTrial.cpp','TerrainTrialGpu.h','TerrainTrialChecks.cpp','PrepareSignature.h','RuntimeGuard.h','..\Shaders\SeasonTerrainSource.hlsl','BuildDrawObserver.ps1')
$taskSourceHashes=@{}
$taskSourceFiles+=@('DrawMeshObserver.cpp','DrawMeshObserverChecks.cpp','ColorMeshObserver.cpp','ColorMeshObserverChecks.cpp')
foreach($taskFile in $taskSourceFiles){$taskSourceHashes[$taskFile]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower()}
$taskRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskZip=Join-Path $taskRoot 'Reverse\.cache\tools\detours-4.0.1.zip'
if((Get-FileHash -LiteralPath $taskZip).Hash -ne '5ab84eb08fb9befeb16ffd04ca283731b1e0e1e53b1947ce7868d4b9654e43fc'){
    throw 'Microsoft Detours 4.0.1 source archive hash differs.'
}
$taskVendor=Join-Path $taskRoot 'Reverse\.cache\tools\detours-4.0.1\Detours-4.0.1\src'
$taskVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$taskVs=& $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$taskVcVersion=(Get-Content -LiteralPath (Join-Path $taskVs 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt')).Trim()
$taskVc=Join-Path $taskVs ('VC\Tools\MSVC\'+$taskVcVersion)
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$taskSdkVersion=(Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$taskCl=Join-Path $taskVc 'bin\Hostx64\x64\cl.exe'
$taskLink=Join-Path $taskVc 'bin\Hostx64\x64\link.exe'
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOut -ErrorAction Stop | Out-Null
$taskIncludes=@("/I$taskVendor",('/I'+(Join-Path $taskVc 'include')))
foreach($taskPart in @('ucrt','um','shared','winrt')){$taskIncludes+=('/I'+(Join-Path $taskSdk "Include\$taskSdkVersion\$taskPart"))}
$taskLibs=@(('/LIBPATH:'+(Join-Path $taskVc 'lib\x64')))
foreach($taskPart in @('ucrt','um')){$taskLibs+=('/LIBPATH:'+(Join-Path $taskSdk "Lib\$taskSdkVersion\$taskPart\x64"))}
$taskFxc=Join-Path $taskSdk "bin\$taskSdkVersion\x64\fxc.exe"
& $taskFxc /nologo /T cs_5_0 /E main /O3 /Fh (Join-Path $taskOut 'TerrainTrialShaderBytecode.h') /Vn SeasonTerrainSourceShader (Join-Path $PSScriptRoot '..\Shaders\SeasonTerrainSource.hlsl')
if($LASTEXITCODE -ne 0){throw 'Minimal land source shader compile failed.'}
$taskIncludes+=("/I$taskOut")
$taskObjects=@()
foreach($taskName in @('detours','modules','disasm','image','creatwth')){
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /MT /O2 /EHsc /W3 $taskIncludes "/Fo$taskObj" (Join-Path $taskVendor ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "Detours $taskName compile failed."};$taskObjects+=$taskObj
}
foreach($taskName in @('DrawObserver','DrawObserverChecks','DrawIndexedObserver','DrawIndexedObserverChecks','DrawInstancedObserver','DrawInstancedObserverChecks','ColorInstancedObserver','ColorInstancedObserverChecks','TextureReadObserver','TextureReadObserverChecks','TerrainTrial','TerrainTrialChecks','DrawMeshObserver','DrawMeshObserverChecks','ColorMeshObserver','ColorMeshObserverChecks')){
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /std:c++20 /MT /O2 /EHsc /W4 $taskIncludes "/Fo$taskObj" (Join-Path $PSScriptRoot ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "$taskName compile failed."}
    $taskArgs=@('/nologo',$taskObj)+$taskObjects+$taskLibs+@('kernel32.lib','user32.lib','advapi32.lib','d3d11.lib','d3dcompiler.lib')
    if($taskName -like '*Checks'){$taskArgs+=('/OUT:'+(Join-Path $taskOut ($taskName+'.exe')))}
    else{$taskArgs+=@('/DLL',('/OUT:'+(Join-Path $taskOut ($taskName+'.dll'))))}
    & $taskLink $taskArgs
    if($LASTEXITCODE -ne 0){throw "$taskName link failed."}
}
$taskManifest=[ordered]@{tool='Microsoft Detours 4.0.1';vendorArchiveSha256=(Get-FileHash -LiteralPath $taskZip).Hash.ToLower();
    dllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'DrawObserver.dll')).Hash.ToLower();
    indexedDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'DrawIndexedObserver.dll')).Hash.ToLower();
    instancedDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'DrawInstancedObserver.dll')).Hash.ToLower();
    colorInstancedDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'ColorInstancedObserver.dll')).Hash.ToLower();
    textureReadDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'TextureReadObserver.dll')).Hash.ToLower();
    terrainTrialDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'TerrainTrial.dll')).Hash.ToLower();
    meshDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'DrawMeshObserver.dll')).Hash.ToLower();
    colorMeshDllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'ColorMeshObserver.dll')).Hash.ToLower();
    ownWarpOnly=$true;gameCallbackVerified=$false;productionReady=$false;hashes=@{}}
foreach($taskFile in $taskSourceFiles){
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower() -ne $taskSourceHashes[$taskFile]){throw "Source changed during build: $taskFile"}
    $taskManifest.hashes[$taskFile]=$taskSourceHashes[$taskFile]
}
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'build.json') -Encoding UTF8
