#if LOCAL_DEVELOPER
using System.Text.Json;
using San14Tenkazu.EnvironmentRules;
namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private async Task RunDeveloperGeography(bool edit)
    {
        if(developerChanging||busy||refreshing||rulePolling||runtimeRulesChecking||environmentChecking||seasonStarting||seasonStopping||HasPendingEdits||developerMenu is not {} window)return;
        developerChanging=true;SetBusy(true,false);UpdateDeveloperControls();
        string? path=null;
        try
        {
            if(game is not{Verified:true} identity||layout is not {} found)throw new InvalidOperationException(L.DeveloperUi.ReadyConnectionRequired);
            if(await Task.Run(()=>GameMemory.ReadPace(identity,found))!=GamePace.Paused)throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
            string directory=Path.Combine(seasonStorageRoot,"environment","diagnostics");Directory.CreateDirectory(directory);
            path=Path.Combine(directory,$"loaded-geography-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{identity.ProcessId}-{Guid.NewGuid():N}.jsonl");
            using var output=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
            var options=new JsonSerializerOptions{IncludeFields=true};
            void Record(object value)=>output.WriteLine(JsonSerializer.Serialize(value,options));
            var data=await Task.Run(()=>GameMemory.ReadSnapshot(identity,found));
            var topology=await Task.Run(()=>GameMemory.ReadDeveloperGeographyTopology(identity,found));
            Record(new{formatVersion=2,utc=DateTime.UtcNow,gameAccess=true,edit,identity.ProcessId,identity.StartTimeUtc,identity.ExeHash,topology,
                data.Date,cities=data.CityIds.Select(id=>new{id,name=data.CityName(id),state=data.Cities[id].State,
                    cityAddress=data.Cities[id].Address,castleAddress=data.CastleByCity.GetValueOrDefault(id)?.Address,tile=data.CityTile(id),
                    gold=data.Cities[id][CityFields.Gold],food=data.Cities[id][CityFields.Food],scale=data.Cities[id][CityFields.Scale],
                    durability=data.CastleByCity.GetValueOrDefault(id)?[CityFields.Durability]}),
                environment=DeveloperEnvironmentEvidence(environmentController?.Status),environmentDll=EnvironmentNativeBundle.DllHash});
            if(edit)await Task.Run(()=>GameMemory.RoundTripDeveloperCities(identity,found,evidence=>Record(evidence)));
            if(game!=identity||!found.SameStorage(await Task.Run(()=>GameMemory.ReadCurrentLayout(identity))))throw new InvalidOperationException(L.RuntimeErrors.Loading);
            Record(new{utc=DateTime.UtcNow,stage="completed",edit});
            window.AppendGeography((edit?L.DeveloperUi.GeographyRoundTripDone:L.DeveloperUi.GeographyReadDone)+path);
        }
        catch(Exception error)
        {
            window.AppendGeography(L.DeveloperUi.ExecutionFailed+error.Message+(path is null?"":" · "+path));
            AddLog("Developer geography: "+error);
        }
        finally{developerChanging=false;SetBusy(false,false);UpdateDeveloperControls();}
    }
    // IncludeFields does not serialize internal wire fields. Project the receipt explicitly
    // so a completed diagnostic cannot silently omit availability, weather or event evidence.
    private static object? DeveloperEnvironmentEvidence(EnvironmentStatus? status)
    {
        if(status is null)return null;
        object? receipt=status.Receipt is {} r?new{
            r.Generation,r.Sequence,r.Applied,r.Frames,r.Thread,r.Date,r.Flags,r.Error,r.Values,
            r.AvailableCities,r.UnavailableCities,Weather=(r.Weather??[]).Select(code=>(int)code).ToArray(),
            r.LastEvent,Events=(r.Events??[]).Where(e=>e.Id!=0).Select(e=>new{
                e.Id,e.Date,e.Kind,e.City,e.Tile,e.Unit,e.Commander,e.TroopsBefore,e.TroopsAfter,
                e.MoraleBefore,e.MoraleAfter,e.FoodBefore,e.FoodAfter}).ToArray(),
            r.NoticesQueued,r.NoticesPending,r.NoticeDate,r.NoticeCity,r.NoticeKind,
            r.TestId,r.TestMode,r.TestState,r.TestCity,r.TestKind,r.TestDate,r.TestError
        }:null;
        return new{status.Phase,status.Requested,status.Actual,status.Restored,Receipt=receipt,status.Error};
    }
}
#endif
