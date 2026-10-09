param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$taskFiles=@('DrawObserver.h','DrawObserver.cpp','TreeMaterialTrial.h','TreeMaterialTrial.cpp','TreeMaterialTrialGpu.h','TreeMaterialTrialChecks.cpp','PrepareSignature.h','RuntimeGuard.h','..\Shaders\SeasonTreeMaterialSource.hlsl','BuildTreeMaterialTrial.ps1')
$taskHashes=@{}
foreach($taskFile in $taskFiles){$taskHashes[$taskFile]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower()}
$taskSourceRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
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
& $taskFxc /nologo /T cs_5_0 /E main /O3 /Fh (Join-Path $taskOut 'TreeMaterialTrialShaderBytecode.h') /Vn SeasonTreeMaterialSourceShader (Join-Path $PSScriptRoot '..\Shaders\SeasonTreeMaterialSource.hlsl')
if($LASTEXITCODE -ne 0){throw 'Tree material shader compile failed.'}
$taskIncludes+=("/I$taskOut")
$taskObjects=@()
foreach($taskName in @('detours','modules','disasm','image','creatwth')){
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /MT /O2 /EHsc /W3 $taskIncludes "/Fo$taskObj" (Join-Path $taskVendor ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "Detours $taskName compile failed."};$taskObjects+=$taskObj
}
foreach($taskName in @('TreeMaterialTrial','TreeMaterialTrialChecks')){
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /std:c++20 /MT /O2 /EHsc /W4 $taskIncludes "/Fo$taskObj" (Join-Path $PSScriptRoot ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "$taskName compile failed."}
    $taskArgs=@('/nologo','/OPT:NOICF',$taskObj)+$taskObjects+$taskLibs+@('kernel32.lib','user32.lib','advapi32.lib','d3d11.lib','dxgi.lib','d3dcompiler.lib')
    if($taskName -like '*Checks'){$taskArgs+=('/OUT:'+(Join-Path $taskOut ($taskName+'.exe')))}else{$taskArgs+=@('/DLL',('/OUT:'+(Join-Path $taskOut ($taskName+'.dll'))))}
    & $taskLink $taskArgs;if($LASTEXITCODE -ne 0){throw "$taskName link failed."}
}
foreach($taskFile in $taskFiles){if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower() -ne $taskHashes[$taskFile]){throw "Source changed during build: $taskFile"}}
$taskSnapshot=Join-Path $taskOut 'sources';New-Item -ItemType Directory -Path $taskSnapshot -ErrorAction Stop | Out-Null
foreach($taskFile in $taskFiles){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskSnapshot ([IO.Path]::GetFileName($taskFile))) -ErrorAction Stop}
[ordered]@{dllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'TreeMaterialTrial.dll')).Hash.ToLower();hashes=$taskHashes;gameCallbackVerified=$false;productionReady=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'build.json') -Encoding UTF8
