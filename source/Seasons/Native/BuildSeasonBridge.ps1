param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$PythonExecutable)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\..\BuildProfiles\Validate.ps1')
$taskGameProfile=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\BuildProfiles\current.json') -Raw | ConvertFrom-Json
$taskGameProfileHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '..\..\BuildProfiles\current.json')).Hash.ToLowerInvariant()
$taskFiles=@('SeasonBridge.cpp','SeasonBridgeProtocol.h','SeasonBridgeChecks.cpp','FrameSignatures.h','PrepareSignature.h','RuntimeGuard.h','SeasonTreeColorGpu.h','..\Shaders\SeasonTreeColorSource.hlsl','BuildSeasonBridge.ps1')
$taskFiles+=@('SeasonSceneChecks.cpp','SeasonSceneGpu.h','SeasonWeatherCapture.h','SeasonWorldGpu.h','SeasonShaderTranslation.h','SeasonShaderProfiles.h','SeasonShaderAdmission.h','SeasonGroundSourceGpu.h','SeasonTreeCatalog.h',
    '..\Assets\tree-catalog.json','..\..\Reverse\season_tree_catalog.py',
    '..\Shaders\SeasonWorldMatrix.hlsl','..\Shaders\SeasonWorldPosition.hlsli','..\Shaders\SeasonGameTree.hlsl','..\Shaders\SeasonGameTerrain.hlsli',
    '..\Shaders\SeasonGameWeather.hlsl','..\Shaders\SeasonGameParameters.hlsli','..\Shaders\SeasonCommon.hlsli','..\Shaders\SeasonTreeCoverage.hlsli','..\Shaders\SeasonWeather.hlsli')
$taskFiles+=@('SeasonRiceGpu.h','SeasonRiceChecks.cpp','..\Shaders\SeasonGameRice.hlsl','..\Shaders\SeasonVegetation.hlsli')
$taskFiles+=@('..\..\Environment\Native\EnvironmentWeather.h')
$taskFiles+=@('SeasonNoticeArt.h')
$taskFiles+=@('GameBuildFacts.g.h')
$taskHashes=@{}
foreach($taskFile in $taskFiles){$taskHashes[$taskFile]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower()}
$taskSourceRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(-not $PythonExecutable){$PythonExecutable=Join-Path $taskSourceRoot 'Reverse\.venv\Scripts\python.exe'}
& $PythonExecutable (Join-Path $taskSourceRoot 'Reverse\season_tree_catalog.py') --check
if($LASTEXITCODE -ne 0){throw 'Managed/native tree catalogs differ.'}
$taskZip=Join-Path $taskSourceRoot 'Reverse\.cache\tools\detours-4.0.1.zip'
if((Get-FileHash -LiteralPath $taskZip).Hash -ne '5ab84eb08fb9befeb16ffd04ca283731b1e0e1e53b1947ce7868d4b9654e43fc'){throw 'Microsoft Detours source archive differs.'}
$taskVendor=Join-Path $taskSourceRoot 'Reverse\.cache\tools\detours-4.0.1\Detours-4.0.1\src'
$taskVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$taskVs=& $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$taskVcVersion=(Get-Content -LiteralPath (Join-Path $taskVs 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt')).Trim()
$taskVc=Join-Path $taskVs ('VC\Tools\MSVC\'+$taskVcVersion)
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$taskSdkVersion=(Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$taskCl=Join-Path $taskVc 'bin\Hostx64\x64\cl.exe';$taskLink=Join-Path $taskVc 'bin\Hostx64\x64\link.exe'
$taskOut=[IO.Path]::GetFullPath($OutputDirectory);New-Item -ItemType Directory -Path $taskOut -ErrorAction Stop | Out-Null
$taskIncludes=@("/I$taskVendor",('/I'+(Join-Path $taskVc 'include')))
foreach($taskPart in @('ucrt','um','shared','winrt')){$taskIncludes+=('/I'+(Join-Path $taskSdk "Include\$taskSdkVersion\$taskPart"))}
$taskLibs=@(('/LIBPATH:'+(Join-Path $taskVc 'lib\x64')))
foreach($taskPart in @('ucrt','um')){$taskLibs+=('/LIBPATH:'+(Join-Path $taskSdk "Lib\$taskSdkVersion\$taskPart\x64"))}
$taskFxc=Join-Path $taskSdk "bin\$taskSdkVersion\x64\fxc.exe"
& $taskFxc /nologo /T cs_5_0 /E main /O3 /Fh (Join-Path $taskOut 'SeasonTreeColorShaderBytecode.h') /Vn SeasonTreeColorShader (Join-Path $PSScriptRoot '..\Shaders\SeasonTreeColorSource.hlsl')
if($LASTEXITCODE -ne 0){throw 'Connected tree color shader compile failed.'}
$taskShaderRoot=Join-Path $PSScriptRoot '..\Shaders'
$taskGameShaders=@(
    @('SeasonWorldMatrix.hlsl','BuildWorldMatrices','cs_5_0','SeasonWorldMatrixBytecode',''),
    @('SeasonGameTree.hlsl','TreeColor','ps_5_0','SeasonTreeColorBytecode',''),
    @('SeasonGameTree.hlsl','TreeDepth','ps_5_0','SeasonTreeDepthBytecode','SEASON_DEPTH=1'),
    @('SeasonGameWeather.hlsl','WeatherSnowVertex','vs_5_0','SeasonWeatherSnowVertexBytecode',''),
    @('SeasonGameWeather.hlsl','WeatherSnow','ps_5_0','SeasonWeatherSnowBytecode',''),
    @('SeasonGameWeather.hlsl','WeatherBounds','cs_5_0','SeasonWeatherBoundsBytecode',''),
    @('SeasonGameWeather.hlsl','WeatherAreaVertex','vs_5_0','SeasonWeatherAreaVertexBytecode',''),
    @('SeasonGameWeather.hlsl','WeatherArea','ps_5_0','SeasonWeatherAreaBytecode',''),
    @('SeasonGameWeather.hlsl','WeatherMarkVertex','vs_5_0','SeasonWeatherMarkVertexBytecode',''),
    @('SeasonGameWeather.hlsl','WeatherMark','ps_5_0','SeasonWeatherMarkBytecode',''),
    @('SeasonGameRice.hlsl','RiceRoots','gs_5_0','SeasonRiceRootsBytecode',''),
    @('SeasonGameRice.hlsl','RiceVertex','vs_5_0','SeasonRiceVertexBytecode',''),
    @('SeasonGameRice.hlsl','RicePlant','gs_5_0','SeasonRicePlantBytecode',''),
    @('SeasonGameRice.hlsl','RiceColor','ps_5_0','SeasonRiceColorBytecode',''))
foreach($taskShader in $taskGameShaders){
    $taskDefines=@();if($taskShader[4]){$taskDefines=@('/D',$taskShader[4])}
    & $taskFxc /nologo /WX $taskDefines /T $taskShader[2] /E $taskShader[1] /O3 /Fh (Join-Path $taskOut ($taskShader[3]+'.h')) /Vn $taskShader[3] (Join-Path $taskShaderRoot $taskShader[0])
    if($LASTEXITCODE -ne 0){throw ('Game shader compile failed: '+$taskShader[1])}
}
function Expand-SeasonShader([string]$taskShaderFile){
    $taskText=[IO.File]::ReadAllText((Join-Path $taskShaderRoot $taskShaderFile))
    return [regex]::Replace($taskText,'(?m)^#include "([A-Za-z0-9_.]+)"\s*$',{param($match) Expand-SeasonShader $match.Groups[1].Value})
}
$taskTerrainText=Expand-SeasonShader 'SeasonGameTerrain.hlsli'
[IO.File]::WriteAllText((Join-Path $taskOut 'SeasonTerrainShaderText.h'),"#pragma once`ninline constexpr char SeasonTerrainShaderText[]=R`"SEASON($taskTerrainText)SEASON`";`n",[Text.UTF8Encoding]::new($false))
$taskIncludes+=("/I$taskOut")
$taskObjects=@()
foreach($taskName in @('detours','modules','disasm','image','creatwth')){
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /MT /O2 /EHsc /W3 $taskIncludes "/Fo$taskObj" (Join-Path $taskVendor ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "Detours $taskName compile failed."};$taskObjects+=$taskObj
}
foreach($taskName in @('SeasonBridge','SeasonBridgeChecks','SeasonSceneChecks','SeasonRiceChecks')){
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /std:c++20 /MT /O2 /EHsc /W4 $taskIncludes "/Fo$taskObj" (Join-Path $PSScriptRoot ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "$taskName compile failed."}
    $taskArgs=@('/nologo','/OPT:NOICF',$taskObj)+$taskObjects+$taskLibs+@('kernel32.lib','user32.lib','advapi32.lib','d3d11.lib','dxgi.lib','d3dcompiler.lib','bcrypt.lib')
    if($taskName -like '*Checks'){$taskArgs+=('/OUT:'+(Join-Path $taskOut ($taskName+'.exe')))}else{$taskArgs+=@('/DLL',('/OUT:'+(Join-Path $taskOut ($taskName+'.dll'))))}
    & $taskLink $taskArgs;if($LASTEXITCODE -ne 0){throw "$taskName link failed."}
}
foreach($taskFile in $taskFiles){if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower() -ne $taskHashes[$taskFile]){throw "Source changed during build: $taskFile"}}
$taskSnapshot=Join-Path $taskOut 'sources';New-Item -ItemType Directory -Path $taskSnapshot -ErrorAction Stop | Out-Null
$taskManifestHashes=@{}
foreach($taskFile in $taskFiles){$taskName=[IO.Path]::GetFileName($taskFile);Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskSnapshot $taskName) -ErrorAction Stop;$taskManifestHashes[$taskName]=$taskHashes[$taskFile]}
& (Join-Path $PSScriptRoot '..\..\BuildProfiles\Validate.ps1')
if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '..\..\BuildProfiles\current.json')).Hash.ToLowerInvariant() -cne $taskGameProfileHash){throw 'Game build profile changed during Native build.'}
[ordered]@{nativeBuild=58;dllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'SeasonBridge.dll')).Hash.ToLower();hashes=$taskManifestHashes;protocolVersion=6;gameProfileId=$taskGameProfile.id;gameProfileSha256=$taskGameProfileHash;nativeContractId=$taskGameProfile.nativeContractId;gameCallbackVerified=$false;productionReady=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'build.json') -Encoding UTF8
