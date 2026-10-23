#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record DeveloperCityRoundTrip(int City, string Name, string Stage, string? Reason,
    long? Gold, long? Durability, long? TestGold, long? TestDurability, string? CitySha256, string? CastleSha256);

internal static partial class GameMemory
{
    // Raw references used by the Native geography checks, including aliases and counties
    // omitted from the editor's editable list. Read-only developer evidence, never repair data.
    internal static object ReadDeveloperGeographyTopology(GameIdentity identity,GameLayout layout)
    {
        GameCapabilities.Require(identity,GameFeature.BasicRead);
        if(!identity.Verified||!IsSameGame(identity)||!layout.SameStorage(ReadCurrentLayout(identity)))throw new InvalidOperationException(L.RuntimeErrors.Loading);
        IntPtr handle=OpenRead(identity.ProcessId);
        try
        {
            var tiles=ReadBytes(handle,layout.TileArray,TileCount*TileStride);
            var buildings=ReadPointers(handle,layout.Tables.Building,MaxBuildings);
            var castles=new List<object>();
            for(int index=0;index<buildings.Length;index++)
            {
                byte[] bytes=new byte[BuildingStride];ulong address=buildings[index];
                if(!TryRead(handle,address,bytes)||BitConverter.ToUInt64(bytes)!=identity.Classes.Building||bytes[BuildingTypeOffset]!=1)continue;
                int tile=BitConverter.ToUInt16(bytes,BuildingTileOffset);if(tile>=TileCount)continue;
                castles.Add(new{index,address,tile,baseId=BitConverter.ToUInt16(tiles,tile*TileStride+TileBaseOffset),county=BitConverter.ToUInt16(tiles,tile*TileStride+TileCountyOffset)});
            }
            var counties=new List<object>();var pointers=ReadPointers(handle,layout.Tables.County,MaxCounties);
            for(int index=0;index<pointers.Length;index++)
            {
                byte[] bytes=new byte[CountyStride];ulong address=pointers[index];
                if(!TryRead(handle,address,bytes)||BitConverter.ToUInt64(bytes)!=identity.Classes.County)continue;
                var county=new CountyRecord(index,address,bytes);
                counties.Add(new{index,address,county.Name,county.HomeCity,county.CurrentCity,county.CenterTile});
            }
            if(!IsSameGame(identity)||!layout.SameStorage(ReadCurrentLayout(identity)))throw new InvalidOperationException(L.RuntimeErrors.Loading);
            return new{layout.BuildingCount,layout.CountyCount,castles,counties};
        }
        finally{CloseHandle(handle);}
    }
    // Developer-menu only. The normal CityEdits/Apply/Undo transaction runs while all game
    // threads are frozen. No frame can consume the temporary values before restoration.
    internal static void RoundTripDeveloperCities(GameIdentity identity, GameLayout layout, Action<DeveloperCityRoundTrip> record)
    {
        GameCapabilities.Require(identity, GameFeature.DataWrite);
        if(!identity.Verified||!IsSameGame(identity))throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if(ReadPace(identity,layout)!=GamePace.Paused)throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        lock(RuntimeRuleLock)
        {
            IntPtr handle=OpenProcess(ProcessQueryInformation|ProcessVmRead|ProcessVmWrite|ProcessVmOperation|0x0800,false,identity.ProcessId);
            if(handle==IntPtr.Zero)throw AccessError(L.Errors.NoWriteAccess);
            try
            {
                using var freeze=RuntimeProcessFreeze.Acquire(handle,identity.ProcessId,[]);
                if(!IsSameGame(identity)||!layout.SameStorage(ReadCurrentLayout(identity)))throw new InvalidOperationException(L.RuntimeErrors.Loading);
                var data=ReadSnapshot(identity,layout);
                RoundTripDeveloperCities(data,new ProcessMemory(handle),identity.Classes,record);
            }
            finally{CloseHandle(handle);}
        }
    }

    internal static void RoundTripDeveloperCities(GameSnapshot data,IRecordMemory memory,GameClasses classes,Action<DeveloperCityRoundTrip> record)
    {
        static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
        foreach(int id in data.CityIds.Where(id=>id>46))
        {
            var city=data.Cities[id];string name=data.CityName(id);
            if(!data.CastleByCity.TryGetValue(id,out var castle)||CityFields.Caps(city[CityFields.Scale]) is not {} caps)
            {record(new(id,name,"skipped","Missing castle or scale",null,null,null,null,null,null));continue;}
            long gold=city[CityFields.Gold],walls=castle[CityFields.Durability];
            static long Next(long value,FieldDef field,long cap)
            {
                if(value<field.Min||value>Math.Min(field.Max,cap))throw new InvalidDataException(field.RangeMessage);
                return value>field.Min?value-1:value+1;
            }
            long testGold=Next(gold,CityFields.Gold,CityFields.Gold.Max),testWalls=Next(walls,CityFields.Durability,CityFields.Durability.Max);
            var evidence=new DeveloperCityRoundTrip(id,name,"prepared",null,gold,walls,testGold,testWalls,Hash(city.Data),Hash(castle.Data));
            record(evidence);
            var plan=CityEdits.Plan(data,id,new([new(CityFields.Gold,testGold),new(CityFields.Durability,testWalls)],[],null));
            if(plan.Edits.Count!=2)throw new InvalidDataException("Developer city plan must contain city and castle");
            foreach(var edit in plan.Edits)
                if(!memory.Read(edit.Address,edit.Expected.Length).SequenceEqual(edit.Expected))throw new InvalidDataException("Developer city changed before write");
            bool applied=false;
            try
            {
                Apply(memory,classes,plan.Edits);applied=true;
                foreach(var edit in Undo.Of(plan.Edits))
                    if(!memory.Read(edit.Address,edit.Expected.Length).SequenceEqual(edit.Expected))throw new InvalidDataException("Developer city readback differs");
                record(evidence with{Stage="changed-readback"});
            }
            finally
            {
                if(applied)
                {
                    var undo=Undo.Of(plan.Edits);
                    foreach(var edit in undo)
                        if(!memory.Read(edit.Address,edit.Expected.Length).SequenceEqual(edit.Expected))throw new InvalidDataException("Developer city recovery ownership unconfirmed");
                    Apply(memory,classes,undo);
                    foreach(var edit in plan.Edits)
                        if(!memory.Read(edit.Address,edit.Expected.Length).SequenceEqual(edit.Expected))throw new InvalidDataException("Developer city recovery readback differs");
                    record(evidence with{Stage="restored-byte-exact"});
                }
            }
        }
    }
}
#endif
