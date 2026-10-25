param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot '..\..\BuildProfiles\Validate.ps1')
$taskGameProfile=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\BuildProfiles\current.json') -Raw | ConvertFrom-Json
$taskGameProfileHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '..\..\BuildProfiles\current.json')).Hash.ToLowerInvariant()
$taskFiles=@('EnvironmentBridge.cpp','EnvironmentProtocol.h','EnvironmentPolicy.h','EnvironmentSignatures.h','EnvironmentWeather.h','EnvironmentChecks.cpp','BuildEnvironmentBridge.ps1','EnvironmentNotification.h','EnvironmentNotificationSignatures.h','EnvironmentNoticeSound.h','GameBuildFacts.g.h')
$taskFiles+=@('EnvironmentWeatherTiming.h','EnvironmentNoticeTrace.h','EnvironmentNoticeTraceStore.h')
$taskHashes=@{};foreach($taskFile in $taskFiles){$taskHashes[$taskFile]=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower()}
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
$taskObjects=@()
foreach($taskName in @('detours','modules','disasm','image','creatwth')) {
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /MT /O2 /EHsc /W3 $taskIncludes "/Fo$taskObj" (Join-Path $taskVendor ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "Detours $taskName compile failed."};$taskObjects+=$taskObj
}
foreach($taskName in @('EnvironmentBridge','EnvironmentChecks')) {
    $taskObj=Join-Path $taskOut ($taskName+'.obj')
    & $taskCl /nologo /c /std:c++20 /utf-8 /MT /O2 /EHsc /W4 $taskIncludes "/Fo$taskObj" (Join-Path $PSScriptRoot ($taskName+'.cpp'))
    if($LASTEXITCODE -ne 0){throw "$taskName compile failed."}
    $taskArgs=@('/nologo','/OPT:NOICF',$taskObj)+$taskObjects+$taskLibs+@('kernel32.lib','user32.lib','advapi32.lib')
    if($taskName -eq 'EnvironmentChecks'){$taskArgs+=('/OUT:'+(Join-Path $taskOut ($taskName+'.exe')))}else{$taskArgs+=@('/DLL',('/OUT:'+(Join-Path $taskOut ($taskName+'.dll'))))}
    & $taskLink $taskArgs;if($LASTEXITCODE -ne 0){throw "$taskName link failed."}
}
& (Join-Path $taskOut 'EnvironmentChecks.exe') (Join-Path $taskOut 'vectors.json') *> (Join-Path $taskOut 'checks.log')
if($LASTEXITCODE -ne 0){throw 'Native environment checks failed; see checks.log.'}
foreach($taskFile in $taskFiles){if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskFile)).Hash.ToLower() -ne $taskHashes[$taskFile]){throw "Source changed during build: $taskFile"}}
$taskSnapshot=Join-Path $taskOut 'sources';New-Item -ItemType Directory -Path $taskSnapshot -ErrorAction Stop | Out-Null
foreach($taskFile in $taskFiles){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskSnapshot $taskFile) -ErrorAction Stop}
& (Join-Path $PSScriptRoot '..\..\BuildProfiles\Validate.ps1')
if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '..\..\BuildProfiles\current.json')).Hash.ToLowerInvariant() -cne $taskGameProfileHash){throw 'Game build profile changed during Native build.'}
[ordered]@{nativeBuild=28;dllSha256=(Get-FileHash -LiteralPath (Join-Path $taskOut 'EnvironmentBridge.dll')).Hash.ToLower();hashes=$taskHashes;protocolVersion=6;gameProfileId=$taskGameProfile.id;gameProfileSha256=$taskGameProfileHash;nativeContractId=$taskGameProfile.nativeContractId;nativeFixtureVerified=$true;liveGameVerified=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'build.json') -Encoding UTF8
