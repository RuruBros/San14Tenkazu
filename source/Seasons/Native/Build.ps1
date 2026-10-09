$ErrorActionPreference = 'Stop'
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$taskVs = & $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $taskVs) { throw 'MSVC x64 build tools are required to build the native season helper.' }
$taskVcvars = Join-Path $taskVs 'VC/Auxiliary/Build/vcvars64.bat'
$taskEnvironment = & cmd.exe /d /c "call `"$taskVcvars`" >nul && set"
if ($LASTEXITCODE -ne 0) { throw 'MSVC environment initialization failed.' }
foreach ($taskLine in $taskEnvironment) {
    if ($taskLine -match '^(PATH|INCLUDE|LIB|LIBPATH)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
}
foreach ($taskName in @('Trace','ShaderInspect','CommandProbe')) {
    $taskSource = Join-Path $PSScriptRoot ($taskName + '.cpp')
    $taskOutput = Join-Path $PSScriptRoot ($taskName + '.obj')
    & cl.exe /nologo /c /O2 /GS- /GR- /EHsc /Zl /Gy "/Fo$taskOutput" $taskSource
    if ($LASTEXITCODE -ne 0) { throw "Native season $taskName build failed." }
}
$taskChecks = Join-Path $PSScriptRoot 'RuntimeChecks.cpp'
$taskChecksExe = Join-Path $PSScriptRoot 'RuntimeChecks.exe'
$taskChecksObj = Join-Path $PSScriptRoot 'RuntimeChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskChecksObj" "/Fe$taskChecksExe" $taskChecks /link d3d11.lib d3dcompiler.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season lifecycle checks build failed.' }
$taskShaderChecks = Join-Path $PSScriptRoot 'ShaderChecks.cpp'
$taskShaderExe = Join-Path $PSScriptRoot 'ShaderChecks.exe'
$taskShaderObj = Join-Path $PSScriptRoot 'ShaderChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskShaderObj" "/Fe$taskShaderExe" $taskShaderChecks /link d3d11.lib d3dcompiler.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season shader checks build failed.' }
$taskTreeChecks = Join-Path $PSScriptRoot 'TreeShaderChecks.cpp'
$taskTreeExe = Join-Path $PSScriptRoot 'TreeShaderChecks.exe'
$taskTreeObj = Join-Path $PSScriptRoot 'TreeShaderChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskTreeObj" "/Fe$taskTreeExe" $taskTreeChecks /link d3d11.lib d3dcompiler.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season tree coverage checks build failed.' }
$taskTextureChecks = Join-Path $PSScriptRoot 'TextureChecks.cpp'
$taskTextureExe = Join-Path $PSScriptRoot 'TextureChecks.exe'
$taskTextureObj = Join-Path $PSScriptRoot 'TextureChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskTextureObj" "/Fe$taskTextureExe" $taskTextureChecks /link d3d11.lib bcrypt.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season texture upload checks build failed.' }
$taskMaskChecks = Join-Path $PSScriptRoot 'TreeMaskChecks.cpp'
$taskMaskExe = Join-Path $PSScriptRoot 'TreeMaskChecks.exe'
$taskMaskObj = Join-Path $PSScriptRoot 'TreeMaskChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskMaskObj" "/Fe$taskMaskExe" $taskMaskChecks /link d3d11.lib d3dcompiler.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season mask sampling checks build failed.' }
$taskTerrainChecks = Join-Path $PSScriptRoot 'TerrainShaderChecks.cpp'
$taskTerrainExe = Join-Path $PSScriptRoot 'TerrainShaderChecks.exe'
$taskTerrainObj = Join-Path $PSScriptRoot 'TerrainShaderChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskTerrainObj" "/Fe$taskTerrainExe" $taskTerrainChecks /link d3d11.lib d3dcompiler.lib bcrypt.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season terrain composition checks build failed.' }
$taskWeatherChecks = Join-Path $PSScriptRoot 'WeatherChecks.cpp'
$taskWeatherExe = Join-Path $PSScriptRoot 'WeatherChecks.exe'
$taskWeatherObj = Join-Path $PSScriptRoot 'WeatherChecks.obj'
& cl.exe /nologo /std:c++20 /O2 /EHsc /W4 "/Fo$taskWeatherObj" "/Fe$taskWeatherExe" $taskWeatherChecks /link d3d11.lib d3dcompiler.lib
if ($LASTEXITCODE -ne 0) { throw 'Native season weather pass checks build failed.' }
