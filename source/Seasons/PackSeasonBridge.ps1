param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$taskBuild=[IO.Path]::GetFullPath($BuildDirectory)
$taskManifest=Get-Content -LiteralPath (Join-Path $taskBuild 'build.json') -Raw | ConvertFrom-Json
if ($taskManifest.protocolVersion -ne 5 -or $taskManifest.dllSha256 -ne '70f8836b2eb0a280784aaa31c53eba02760a83e1fa6bb94914089ea488f546ff') { throw 'Unverified season bundle build' }
if ((Get-FileHash -LiteralPath (Join-Path $taskBuild 'build.json')).Hash.ToLowerInvariant() -ne '4226cf9eaf5685b68e7da14f125d81de173e567be3edf8ee8eaab28de73a3673') { throw 'Native manifest differs' }
if ((Get-FileHash -LiteralPath (Join-Path $taskBuild 'SeasonBridge.dll')).Hash.ToLowerInvariant() -ne $taskManifest.dllSha256) { throw 'Native DLL differs' }
foreach ($taskProperty in $taskManifest.hashes.PSObject.Properties) {
    $taskFolder=switch ([IO.Path]::GetExtension($taskProperty.Name)) { '.hlsl' {'Shaders'} '.hlsli' {'Shaders'} '.json' {'Assets'} '.py' {'..\Reverse'} default {'Native'} }
    $taskCurrent=Join-Path $PSScriptRoot (Join-Path $taskFolder $taskProperty.Name)
    if ((Get-FileHash -LiteralPath $taskCurrent).Hash.ToLowerInvariant() -ne $taskProperty.Value -or
        (Get-FileHash -LiteralPath (Join-Path $taskBuild ('sources\'+$taskProperty.Name))).Hash.ToLowerInvariant() -ne $taskProperty.Value) { throw ('Native source differs: '+$taskProperty.Name) }
}
Add-Type -AssemblyName System.IO.Compression
$taskOutput=Join-Path $PSScriptRoot 'Bundle\SeasonBridge.zip'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
$taskStream=[IO.File]::Open($taskOutput,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$taskArchive=[IO.Compression.ZipArchive]::new($taskStream,[IO.Compression.ZipArchiveMode]::Create)
try {
    $taskEntries=@('SeasonBridge.dll','build.json')+@($taskManifest.hashes.PSObject.Properties.Name | Sort-Object | ForEach-Object { 'sources/'+$_ })
    foreach ($taskName in $taskEntries) {
        $taskEntry=$taskArchive.CreateEntry($taskName,[IO.Compression.CompressionLevel]::Optimal)
        $taskEntry.LastWriteTime=[DateTimeOffset]::new(2026,10,7,0,0,0,[TimeSpan]::Zero)
        $taskEntryStream=$taskEntry.Open()
        try { $taskBytes=[IO.File]::ReadAllBytes((Join-Path $taskBuild $taskName));$taskEntryStream.Write($taskBytes,0,$taskBytes.Length) }
        finally { $taskEntryStream.Dispose() }
    }
}
finally { $taskArchive.Dispose();$taskStream.Dispose() }
Get-FileHash -LiteralPath $taskOutput
