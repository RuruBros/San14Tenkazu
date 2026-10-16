[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Python,
    [string]$AuditModulePath,
    [string]$Output
)
$ErrorActionPreference='Stop'
$taskRepository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$Output) { $Output=Join-Path $taskRepository ('.cache/compatibility-audit/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')) }
$taskOutput=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $taskOutput){throw 'Use a new output directory; prior evidence must remain immutable.'}
if($env:GITHUB_ACTIONS -eq 'true'){throw 'This local verifier builds a developer flavor. GitHub builds must use the public release workflow.'}
New-Item -ItemType Directory -Path $taskOutput | Out-Null
function Hash([string]$taskPath){
    $taskHasher=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($taskHasher.ComputeHash([IO.File]::ReadAllBytes($taskPath))).Replace('-','').ToLowerInvariant()}finally{$taskHasher.Dispose()}
}
function Sources {
    $taskFiles=@(& git -C $taskRepository ls-files --cached --others --exclude-standard -- source | Sort-Object -Unique)
    if($LASTEXITCODE -ne 0 -or !$taskFiles.Count){throw 'Cannot inventory source files'}
    $taskHashes=[ordered]@{}
    foreach($taskFile in $taskFiles){$taskHashes[$taskFile]=Hash (Join-Path $taskRepository $taskFile)}
    return ConvertTo-Json -InputObject $taskHashes -Compress -Depth 5
}
$taskStages=New-Object System.Collections.Generic.List[object]
function Run([string]$taskName,[string]$taskCommand,[string[]]$taskArguments,[bool]$taskCollectFailure=$false){
    $taskLog=Join-Path $taskOutput ($taskName+'.log')
    Write-Host ('Running '+$taskName+' (offline fixtures only)')
    # Windows PowerShell turns native stderr into ErrorRecords. Exit status, not stderr presence, decides success.
    $taskPrior=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try{& $taskCommand @taskArguments *> $taskLog;$taskCode=$LASTEXITCODE}finally{$ErrorActionPreference=$taskPrior}
    $taskStages.Add([ordered]@{name=$taskName;exitCode=$taskCode;log=[IO.Path]::GetFileName($taskLog);sha256=(Hash $taskLog);gameAccess=$false})
    if($taskCode -ne 0){Get-Content -LiteralPath $taskLog -Tail 30;if(!$taskCollectFailure){throw ('Verification failed: '+$taskName)}}
}
$taskTempBefore=$env:TEMP;$taskTmpBefore=$env:TMP;$taskModulesBefore=$env:PYTHONPATH
Push-Location $taskRepository
$taskBefore=Sources
[IO.File]::WriteAllText((Join-Path $taskOutput 'source-hashes.json'),$taskBefore)
try {
    $env:TEMP=Join-Path $taskOutput 'tmp';$env:TMP=$env:TEMP;New-Item -ItemType Directory -Path $env:TEMP | Out-Null
    if($AuditModulePath){$env:PYTHONPATH=$AuditModulePath}
    Run 'generated-drift' $Python @('source/BuildProfiles/generate.py','--check')
    Run 'generator-tests' $Python @('-m','unittest','discover','-s','source/BuildProfiles','-p','test_generate.py','-v')
    Run 'version-matcher-tests' $Python @('-m','unittest','discover','-s','source/Reverse','-p','test_version_audit.py','-v')
    # This excluded production test opens the real user's game, so it never belongs to an offline verifier.
    $taskFilter='(FullyQualifiedName~CompatibilityTests|FullyQualifiedName~SettingsMigrationTests|FullyQualifiedName~GameClassesTests|FullyQualifiedName~GameBuildProfile|FullyQualifiedName~RuntimeRule|FullyQualifiedName~RuntimeTrait|FullyQualifiedName~KnownBuilds|FullyQualifiedName~PublicLibraryLoader|FullyQualifiedName~RuleProfile|FullyQualifiedName~Season|FullyQualifiedName~EnvironmentTests|FullyQualifiedName~GameCallTests|FullyQualifiedName~CalendarTests|FullyQualifiedName~FieldTests|FullyQualifiedName~WriteTests|FullyQualifiedName~WriteRecoveryTests|FullyQualifiedName~SnapshotReadBufferTests|FullyQualifiedName~StringsTests|FullyQualifiedName~StringResourceBaselineTests)&FullyQualifiedName!~ProductionDateSourceReadsTheCurrentVerifiedGameWithoutWriting'
    Run 'managed-offline' 'dotnet' @('test','source/Tests/San14Tenkazu.Tests.csproj','--no-restore','-c','Release','-p:NuGetAudit=false','--filter',$taskFilter,'--results-directory',(Join-Path $taskOutput 'managed'),'--logger','trx;LogFileName=offline.trx') $true
    [xml]$taskTrx=Get-Content -LiteralPath (Join-Path $taskOutput 'managed/offline.trx')
    $taskManagedPassed=[int]$taskTrx.TestRun.ResultSummary.Counters.total -gt 0 -and [int]$taskTrx.TestRun.ResultSummary.Counters.total -eq [int]$taskTrx.TestRun.ResultSummary.Counters.passed
    foreach($taskFlavor in @('dev','public')){
        $taskDev=$taskFlavor -eq 'dev';$taskBool=$taskDev.ToString().ToLowerInvariant();$taskBuild=Join-Path $taskOutput ('checks-'+$taskFlavor)
        Run ($taskFlavor+'-build') 'dotnet' @('build','source/RenderChecks/San14Tenkazu.RenderChecks.csproj','--no-restore','-c','Release','-p:NuGetAudit=false',('-p:LocalDeveloperPackage='+$taskBool),('-p:DeveloperPackageBuild='+$taskBool),('-p:PublicRelease='+(!$taskDev).ToString().ToLowerInvariant()),'-o',$taskBuild)
        $taskChecks=Join-Path $taskBuild 'San14Tenkazu.Tests.dll'
        Run ($taskFlavor+'-menu') 'dotnet' @($taskChecks,'--developer-menu-check',$taskDev.ToString())
        Run ($taskFlavor+'-season-bundle') 'dotnet' @($taskChecks,'--season-bundle-check','source/Seasons')
        Run ($taskFlavor+'-environment-bundle') 'dotnet' @($taskChecks,'--environment-bundle-check','source/Environment')
    }
    $taskDevChecks=Join-Path $taskOutput 'checks-dev/San14Tenkazu.Tests.dll'
    Run 'runtime-memory-wpf' 'dotnet' @($taskDevChecks,'--runtime-rules-offline',(Join-Path $taskOutput 'runtime-memory'))
    Run 'runtime-persistence-wpf' 'dotnet' @($taskDevChecks,'--runtime-rule-persistence',(Join-Path $taskOutput 'runtime-persistence'))
    Run 'settings-import-wpf' 'dotnet' @($taskDevChecks,'--settings-import-offline',(Join-Path $taskOutput 'settings-import'))
    if((Sources) -cne $taskBefore){throw 'Sources changed during verification; these results cannot certify the final tree.'}
    $taskProfile=Get-Content -LiteralPath source/BuildProfiles/current.json -Raw | ConvertFrom-Json
    $taskPassed=$taskManagedPassed -and @($taskStages|Where-Object{$_.exitCode -ne 0}).Count -eq 0
    $taskReport=[ordered]@{schemaVersion=1;utc=[DateTime]::UtcNow.ToString('o');gameAccess=$false;passed=$taskPassed;profileId=$taskProfile.id;profileSha256=(Hash 'source/BuildProfiles/current.json');exeSha256=$taskProfile.exeSha256;sourceInventorySha256=(Hash (Join-Path $taskOutput 'source-hashes.json'));managedTotal=[int]$taskTrx.TestRun.ResultSummary.Counters.total;managedPassed=[int]$taskTrx.TestRun.ResultSummary.Counters.passed;managedAllPassed=$taskManagedPassed;stages=$taskStages.ToArray();authorization='Regression evidence only. No new build registration, migration route, live execution, publish or deployment is authorized by this report.'}
    [IO.File]::WriteAllText((Join-Path $taskOutput 'report.json'),(ConvertTo-Json -InputObject $taskReport -Depth 12))
    if(!$taskPassed){throw ('Offline verification has failures; no certification: '+(Join-Path $taskOutput 'report.json'))}
    Write-Host ('PASS offline compatibility verification: '+(Join-Path $taskOutput 'report.json'))
}
finally{$env:TEMP=$taskTempBefore;$env:TMP=$taskTmpBefore;$env:PYTHONPATH=$taskModulesBefore;Pop-Location}
