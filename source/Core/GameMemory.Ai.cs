namespace San14Tenkazu;

internal static partial class GameMemory
{
    // The AI's trees in the running game, each node checked against the game's own file (AiMemory.Read), read only.
    // Only on the verified build: the AI manager's pointer (AiMemory.ManagerPointerRva) was read on it.
    internal static AiLiveTree[] ReadAiTrees(GameIdentity identity, AiTree[] file) =>
        ReadAi(identity, memory => AiMemory.Read(memory, identity.ModuleBase, file));

    // A record of an AI write as the game holds it now, for [되돌리기] (the snapshot has no AI nodes); null when it
    // cannot be read.
    internal static byte[]? ReadAiRecord(GameIdentity identity, RecordEdit edit)
    {
        try { return ReadAi(identity, memory => memory.Read(edit.Address, edit.Expected.Length)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    // Something read from the game's memory through IRecordMemory (the AI's numbers: AiMemory.Value, Differences).
    internal static T ReadAi<T>(GameIdentity identity, Func<IRecordMemory, T> read)
    {
        if (!identity.Verified) throw new InvalidOperationException(L.Ai.Unverified);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return read(new ProcessMemory(handle)); }
        finally { CloseHandle(handle); }
    }
}
