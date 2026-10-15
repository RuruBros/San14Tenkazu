#if LOCAL_DEVELOPER
using System.Text.Json;
using System.Text.Json.Serialization;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private CancellationTokenSource? compatibilityCancellation;
    private Task? compatibilityTask;
    private CompatibilityReport? compatibilityResult;
    private string? compatibilityLogPath;
    private async Task ReadDeveloperCompatibility()
    {
        if(developerChanging||busy||refreshing||rulePolling||runtimeRulesChecking||environmentChecking||seasonStarting||seasonStopping||developerMenu is not {} window)return;
        developerChanging=true;
        using var cancellation=new CancellationTokenSource();compatibilityCancellation=cancellation;SetBusy(true,false);UpdateDeveloperControls();
        var connected=game;
        CompatibilityReport report;
        try
        {
            var identity=connected??await Task.Run(GameMemory.FindGame,cancellation.Token);
            // The worker only reads. The UI's connection is compared again after it completes.
            report=await Task.Run(()=>GameMemory.ReadCompatibilityDiagnostic(identity,()=>true,cancellation.Token));
            if(game!=connected||cancellation.IsCancellationRequested)
                report=report with{Consistent=false,Error="연결 변경 또는 취소: 미확정 결과입니다.",Capabilities=[]};
        }
        catch(Exception error){report=new(false,error is OperationCanceledException?"취소: 미확정 결과입니다.":error.Message,null,null,[]);}
        try
        {
            compatibilityResult=report;
            string directory=Path.Combine(seasonStorageRoot,"compatibility","diagnostics");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,$"compatibility-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            // Migration is exercised only on a new scratch copy; no original settings file is opened for writing.
            string? migrationError=null;
            if(report.Consistent&&report.First is {} first)
                try
                {
                    var file=new RuntimeRulePreferenceFile(Path.Combine(directory,"scratch-"+Guid.NewGuid().ToString("N")));
                    var original=new RuntimeRulePreference(1,first.Identity.ExeHash,RuntimeRuleSettings.Original,false);
                    file.Save(original);string stamp=file.Stamp();
                    var migrated=SettingsMigration.Runtime(original,first.Identity.ExeHash);
                    file.Save(migrated,preservePrevious:true,expectedStamp:stamp);
                    if(file.Read()!=migrated)throw new IOException("Scratch migration readback differs");
                }
                catch(Exception error){migrationError=error.Message;}
            var json=new JsonSerializerOptions{WriteIndented=true};json.Converters.Add(new JsonStringEnumConverter());
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                await JsonSerializer.SerializeAsync(output,new{formatVersion=1,utc=DateTime.UtcNow,readOnly=true,gameAccess=true,
                    nativeExecutionTested=false,settingsMigration=new{scope="new scratch files only",attempted=report.Consistent,error=migrationError},
                    report.Consistent,report.Error,first=Evidence(report.First),second=Evidence(report.Second),report.Capabilities},json);
            compatibilityLogPath=path;
            window.AppendCompatibility((report.Consistent?"두 번 읽기 일치":"미확정: "+report.Error)+" · "+path);
            if(migrationError is not null)window.AppendCompatibility("임시 복사본 설정 이관 실패: "+migrationError);
        }
        catch(Exception error){window.AppendCompatibility("진단 로그 저장 실패: "+error.Message);}
        finally{compatibilityCancellation=null;developerChanging=false;SetBusy(false,false);UpdateDeveloperControls();}
    }
    private static object? Evidence(CompatibilityRead? read)=>read is null?null:new{
        read.Identity.ProcessId,read.Identity.StartTimeUtc,read.Identity.ModuleBase,read.Identity.ExeHash,read.Identity.Verified,
        profile=GameBuildProfiles.Find(read.Identity.ExeHash) is {} p?new{p.Id,p.Sha256,p.LayoutId,p.NativeContractId}:null,
        read.Identity.Classes,read.Layout,read.SnapshotSha256,read.CatalogSha256,read.Date,read.Hour,read.Pace,read.GrowthKnown,read.Engine,read.Tables,
        checks=read.Checks.Select(c=>new{c.Group,c.What,c.Fit,c.Total,c.Needed,c.Tie,c.EvidenceKnown,c.Passed})};
}
#endif
