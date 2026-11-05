namespace San14Tenkazu;

internal enum NativeCommandKind { Camera, Calendar, Item, Alliance, Give, Capture, Transfer, Revival, Independence, Surrender, Demo }

internal sealed partial class EditorWindow
{
    private async Task<T> RunNativeCommandAsync<T>(NativeCommandKind kind, GameIdentity identity, GameLayout found,
        object target, Func<T> command, bool single = true)
    {
#if LOCAL_DEVELOPER
        var trial = await BeginNativeCommandTrialAsync(kind, identity, found, target, single);
        try
        {
#endif
            T result = await Task.Run(command);
#if LOCAL_DEVELOPER
            if (trial is not null)
            {
                if (kind == NativeCommandKind.Camera && result is string why) throw new InvalidOperationException(why);
                await FinishNativeCommandTrialAsync(trial, identity, found);
            }
#endif
            return result;
#if LOCAL_DEVELOPER
        }
        catch (Exception error)
        {
            if (trial is not null) FailNativeCommandTrial(trial, error);
            throw;
        }
#endif
    }

    private Task RunNativeCommandAsync(NativeCommandKind kind, GameIdentity identity, GameLayout found,
        object target, Action command, bool single = true) => RunNativeCommandAsync(kind, identity, found, target, () =>
    {
        command();
        return true;
    }, single);
}
