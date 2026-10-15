using System.Buffers.Binary;
using San14Tenkazu.Seasons;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    // A new read-only handle and live world chain each time: no cached GameLayout
    // date or editor-map update is involved. This is not ownership of GPU objects.
    internal static SeasonSnapshot ReadSeasonSnapshot(GameIdentity identity)
    {
        if (!identity.Verified || !identity.ExeHash.Equals(VerifiedExeHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The season renderer requires the verified executable");
        if (!IsSameGame(identity)) throw new InvalidOperationException("The season game process changed");
        IntPtr handle=OpenRead(identity.ProcessId);
        try
        {
            SeasonSnapshot Read()
            {
                ulong Pointer(ulong address)
                {
                    ulong value=ReadPointers(handle,address,1)[0];
                    if (value==0) throw new InvalidDataException("Season world/render pointer is unavailable");return value;
                }
                ulong world=checked(Pointer(identity.ModuleBase+BasePointerRva)+BaseAdjust);
                ulong state=Pointer(world+StatePointer);
                if (!HasVtable(handle,state,identity.Classes.World)) throw new InvalidDataException("Season world class changed");
                byte[] clock=ReadBytes(handle,state+StateYearOffset,5);
                if (!SeasonDate.TryCreate(BinaryPrimitives.ReadUInt16LittleEndian(clock),clock[2],clock[3],out var date))
                    throw new InvalidDataException("Season game calendar is invalid");
                ulong manager=Pointer(identity.ModuleBase+GameBuildFacts.Scene.ManagerRva);
                if (BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(handle,manager+GameBuildFacts.Scene.ManagerStateOffset,4))!=0)
                    throw new InvalidDataException("A loaded season scene is required");
                ulong scene=Pointer(manager+GameBuildFacts.Scene.SceneOffset),parent=Pointer(scene+GameBuildFacts.Scene.ParentOffset),driver=Pointer(parent+GameBuildFacts.Scene.DriverOffset);
                if (!HasVtable(handle,driver,identity.ModuleBase+GameBuildFacts.Scene.DriverClassRva))
                    throw new InvalidDataException("Season scene driver class changed");
                var result=new SeasonSnapshot(new(identity.ProcessId,identity.StartTimeUtc,identity.ModuleBase,
                    world,state,manager,scene,driver,Pointer(driver+GameBuildFacts.Scene.DeviceOffset),Pointer(driver+GameBuildFacts.Scene.ContextOffset)),date);
                result.Validate();return result;
            }
            // An inconsistent read is not fed into native rendering. Retry this
            // bounded read immediately, without delaying the independent timer.
            for (int attempt=0;attempt<3;attempt++)
            {
                var before=Read();var after=Read();
                if (before==after && IsSameGame(identity)) return after;
            }
            throw new InvalidDataException("Season date/scene changed during snapshot read");
        }
        finally { CloseHandle(handle); }
    }
}

internal sealed class GameSeasonDateSource(GameIdentity identity) : ISeasonDateSource
{
    public async ValueTask<SeasonSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        await Task.Run(()=>GameMemory.ReadSeasonSnapshot(identity),cancellationToken).ConfigureAwait(false);
}
