[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Season','Environment')][string]$Kind,
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{64}$')][string]$DllSha256,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{64}$')][string]$ManifestSha256
)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Validate.ps1')
$taskSource=Split-Path $PSScriptRoot -Parent
$taskFolder=if($Kind -eq 'Season'){'Seasons'}else{'Environment'}
$taskDll=$Kind+'Bridge.dll'
$taskBuild=[IO.Path]::GetFullPath($BuildDirectory)
$taskManifestPath=Join-Path $taskBuild 'build.json'
if((Get-FileHash -LiteralPath $taskManifestPath).Hash.ToLowerInvariant() -cne $ManifestSha256){throw 'Native manifest differs from the explicitly verified pin.'}
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
$taskProfile=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'current.json') -Raw | ConvertFrom-Json
$taskProfileHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'current.json')).Hash.ToLowerInvariant()
$taskProtocol=6
if($taskManifest.protocolVersion -ne $taskProtocol -or $taskManifest.dllSha256 -cne $DllSha256 -or
    $taskManifest.gameProfileId -cne $taskProfile.id -or $taskManifest.gameProfileSha256 -cne $taskProfileHash -or
    $taskManifest.nativeContractId -cne $taskProfile.nativeContractId){throw 'Native protocol/profile/DLL pin differs.'}
if((Get-FileHash -LiteralPath (Join-Path $taskBuild $taskDll)).Hash.ToLowerInvariant() -cne $DllSha256){throw 'Native DLL differs.'}
foreach($taskProperty in $taskManifest.hashes.PSObject.Properties){
    $taskName=$taskProperty.Name
    if($taskName -ne [IO.Path]::GetFileName($taskName)){throw 'Native source name is not a basename.'}
    $taskCurrent=if($Kind -eq 'Season' -and $taskName -eq 'EnvironmentWeather.h'){
        Join-Path $taskSource ('Environment/Native/'+$taskName)
    }elseif($Kind -eq 'Environment'){
        Join-Path $taskSource ('Environment/Native/'+$taskName)
    }else{
        $taskPart=switch([IO.Path]::GetExtension($taskName)){'.hlsl'{'Seasons/Shaders'} '.hlsli'{'Seasons/Shaders'} '.json'{'Seasons/Assets'} '.py'{'Reverse'} default{'Seasons/Native'}}
        Join-Path $taskSource ($taskPart+'/'+$taskName)
    }
    if((Get-FileHash -LiteralPath $taskCurrent).Hash.ToLowerInvariant() -cne $taskProperty.Value -or
        (Get-FileHash -LiteralPath (Join-Path $taskBuild ('sources/'+$taskName))).Hash.ToLowerInvariant() -cne $taskProperty.Value){throw ('Native source differs: '+$taskName)}
}
$taskOutput=Join-Path $taskSource ($taskFolder+'/Bundle/'+$Kind+'Bridge.zip')
$taskStaging=$taskOutput+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
Add-Type -AssemblyName System.IO.Compression
try{
    $taskStream=[IO.File]::Open($taskStaging,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $taskArchive=[IO.Compression.ZipArchive]::new($taskStream,[IO.Compression.ZipArchiveMode]::Create)
    try{
        foreach($taskName in @($taskDll,'build.json')+@($taskManifest.hashes.PSObject.Properties.Name|Sort-Object|ForEach-Object{'sources/'+$_})){
            $taskEntry=$taskArchive.CreateEntry($taskName,[IO.Compression.CompressionLevel]::Optimal)
            $taskEntry.LastWriteTime=[DateTimeOffset]::new(2026,10,16,0,0,0,[TimeSpan]::Zero)
            $taskWriter=$taskEntry.Open()
            try{$taskBytes=[IO.File]::ReadAllBytes((Join-Path $taskBuild $taskName));$taskWriter.Write($taskBytes,0,$taskBytes.Length)}finally{$taskWriter.Dispose()}
        }
    }finally{$taskArchive.Dispose();$taskStream.Dispose()}
    Move-Item -LiteralPath $taskStaging -Destination $taskOutput -Force
}finally{if(Test-Path -LiteralPath $taskStaging){Remove-Item -LiteralPath $taskStaging}}
Get-FileHash -LiteralPath $taskOutput
