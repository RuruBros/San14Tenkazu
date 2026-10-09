param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$taskFiles=@('BuildSeasonPassChecks.ps1','SeasonWorldGpu.h','SeasonWorldChecks.cpp','SeasonTreeGameChecks.cpp','SeasonTreeCoverageChecks.cpp','SeasonTreeCatalog.h','SeasonShaderTranslation.h','SeasonTerrainGameChecks.cpp','RuntimeGuard.h',
    '../Shaders/SeasonWorldMatrix.hlsl','../Shaders/SeasonWorldPosition.hlsli','../Shaders/SeasonGameTree.hlsl',
    '../Shaders/SeasonGameParameters.hlsli','../Shaders/SeasonTreeCoverage.hlsli','../Shaders/SeasonCommon.hlsli')
$taskHashes=@{}
foreach($taskFile in $taskFiles){$taskHashes[$taskFile]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLowerInvariant()}
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOut -ErrorAction Stop | Out-Null
$taskVswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskVs=& $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$taskVcvars=Join-Path $taskVs 'VC/Auxiliary/Build/vcvars64.bat'
$taskEnvironment=& cmd.exe /d /c "call `"$taskVcvars`" >nul && set"
if($LASTEXITCODE -ne 0){throw 'MSVC environment initialization failed.'}
foreach($taskLine in $taskEnvironment){if($taskLine -match '^(PATH|INCLUDE|LIB|LIBPATH)=(.*)$'){
    [Environment]::SetEnvironmentVariable($Matches[1],$Matches[2],'Process')
}}
$taskShaders=Join-Path $PSScriptRoot '../Shaders'
& fxc.exe /nologo /WX /T cs_5_0 /E BuildWorldMatrices /O3 /Fh (Join-Path $taskOut 'SeasonWorldMatrixBytecode.h') /Vn SeasonWorldMatrixBytecode (Join-Path $taskShaders 'SeasonWorldMatrix.hlsl')
if($LASTEXITCODE -ne 0){throw 'Game transform shader compile failed.'}
& cl.exe /nologo /std:c++20 /MT /O2 /EHsc /W4 /WX "/I$taskOut" "/Fo$taskOut/SeasonWorldChecks.obj" "/Fe$taskOut/SeasonWorldChecks.exe" (Join-Path $PSScriptRoot 'SeasonWorldChecks.cpp') /link d3d11.lib d3dcompiler.lib
if($LASTEXITCODE -ne 0){throw 'Game transform checks build failed.'}
foreach($taskPass in @('TreeColor','TreeDepth')){
    $taskDefines=@();if($taskPass -eq 'TreeDepth'){$taskDefines=@('/D','SEASON_DEPTH=1')}
    & fxc.exe /nologo /WX $taskDefines /T ps_5_0 /E $taskPass /O3 /Fo (Join-Path $taskOut ($taskPass+'.cso')) (Join-Path $taskShaders 'SeasonGameTree.hlsl')
    if($LASTEXITCODE -ne 0){throw "Game $taskPass compile failed."}
}
& cl.exe /nologo /std:c++20 /MT /O2 /EHsc /W4 /WX "/I$taskOut" "/Fo$taskOut/SeasonTreeGameChecks.obj" "/Fe$taskOut/SeasonTreeGameChecks.exe" (Join-Path $PSScriptRoot 'SeasonTreeGameChecks.cpp') /link d3d11.lib d3dcompiler.lib
if($LASTEXITCODE -ne 0){throw 'Game tree shader checks build failed.'}
& cl.exe /nologo /std:c++20 /MT /O2 /EHsc /W4 /WX "/I$taskOut" "/Fo$taskOut/SeasonTreeCoverageChecks.obj" "/Fe$taskOut/SeasonTreeCoverageChecks.exe" (Join-Path $PSScriptRoot 'SeasonTreeCoverageChecks.cpp') /link d3d11.lib d3dcompiler.lib bcrypt.lib
if($LASTEXITCODE -ne 0){throw 'Game tree coverage checks build failed.'}
& cl.exe /nologo /std:c++20 /MT /O2 /EHsc /W4 /WX "/I$taskOut" "/Fo$taskOut/SeasonTerrainGameChecks.obj" "/Fe$taskOut/SeasonTerrainGameChecks.exe" (Join-Path $PSScriptRoot 'SeasonTerrainGameChecks.cpp') /link d3d11.lib d3dcompiler.lib
if($LASTEXITCODE -ne 0){throw 'Game terrain shader checks build failed.'}
foreach($taskFile in $taskFiles){
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLowerInvariant() -ne $taskHashes[$taskFile]){throw "Source changed during checks build: $taskFile"}
}
$taskExecutables=@{}
foreach($taskName in @('SeasonWorldChecks','SeasonTreeGameChecks','SeasonTreeCoverageChecks','SeasonTerrainGameChecks')){
    $taskExecutables[$taskName]=(Get-FileHash -LiteralPath (Join-Path $taskOut ($taskName+'.exe'))).Hash.ToLowerInvariant()
}
[ordered]@{sourceHashes=$taskHashes;executables=$taskExecutables;gameAttached=$false;productionBuild=$false} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'checks-build.json') -Encoding UTF8
