#if LOCAL_DEVELOPER
namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal sealed record NativeTrialCamera(double X, double Z, double TargetX, double TargetZ)
    {
        internal bool AtTarget => Math.Abs(X - TargetX) <= 32 && Math.Abs(Z - TargetZ) <= 32;
    }

    internal static NativeTrialCamera ReadNativeTrialCamera(GameIdentity identity, int tile, bool requireIdle)
    {
        if (GameCommandProfile.Find(identity) is null || tile is < 0 or >= TileCount)
            throw new InvalidOperationException(L.Calls.WhyUnverified);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            if (NovemberCamera.Check(handle, identity, requireIdle) is { } refused) throw new InvalidOperationException(refused);
            ulong main = identity.ModuleBase + NovemberCamera.Keeper + MainCamera;
            byte[] position = ReadBytes(handle, main + 0x50, 24);
            float x0 = BitConverter.ToSingle(ReadBytes(handle, identity.ModuleBase + 0x1A23578, 4));
            float z0 = BitConverter.ToSingle(ReadBytes(handle, identity.ModuleBase + 0x2144AD0, 4));
            double x = BitConverter.ToDouble(position, 0), z = BitConverter.ToDouble(position, 16);
            if (!double.IsFinite(x) || !double.IsFinite(z) || !float.IsFinite(x0) || !float.IsFinite(z0))
                throw new InvalidDataException(L.Errors.StateMoved);
            return new(x, z, x0 + 702d * (tile % 220),
                z0 + Math.Sqrt(3) * 468 * (tile / 220 + (tile % 2 == 0 ? 0.5 : 0)));
        }
        finally { CloseHandle(handle); }
    }
}
#endif
