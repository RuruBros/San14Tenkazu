using System.Buffers.Binary;
using System.Text;

namespace San14Tenkazu;

internal enum GameSessionPhase { Unknown, Starting, Title, Transition, Playing, Unsupported }

internal sealed record GameSessionEntry(ulong Address, ulong VtableRva, string Name, int Leaving);
internal sealed record GameSessionObservation(GameSessionPhase Phase, ulong Stack, ulong Pending,
    GameSessionEntry[] Entries, string? Error = null)
{
    internal bool IsPlaying => Phase == GameSessionPhase.Playing;

    internal bool HasPendingChangeOn(GameSessionObservation previous, GameSessionLayout layout) =>
        Phase == GameSessionPhase.Transition && Pending == 1 && Error is null &&
        previous.IsPlaying && Stack != 0 && Stack == previous.Stack &&
        GameSessionReader.Classify(Entries, 0, layout) == GameSessionPhase.Playing &&
        Entries.Take(3).SequenceEqual(previous.Entries.Take(3));

    internal string Evidence => $"phase={Phase}, stack=0x{Stack:X}, pending={Pending}, states=[{string.Join(", ", Entries.Select(e => $"{e.Name}@0x{e.Address:X}/vt=0x{e.VtableRva:X}/leaving={e.Leaving}"))}], error={Error}";
    internal string WaitingMessage => Phase switch
    {
        GameSessionPhase.Title => L.Connection.TitleWaiting,
        GameSessionPhase.Unsupported => L.Connection.BuildUnsupported,
        _ => L.Connection.SessionWaiting
    };
}

internal sealed class GameSessionUnavailableException(GameSessionObservation observation)
    : InvalidOperationException(observation.WaitingMessage)
{
    internal GameSessionObservation Observation => observation;
}

internal sealed record GameSessionLayout(ulong Manager, ulong Getter, string GetterHex, ulong Contains,
    ulong Root, ulong Motor, ulong Title, ulong Game)
{
    internal byte[] GetterCode { get; } = Convert.FromHexString(GetterHex);
}

// The active stack is separate from the world tables, which are populated on the title
// screen too. Each supported build binds the manager, guards and main-state RTTI.
internal static class GameSessionReader
{
    internal const ulong ManagerRva = 0x1B620B0;
    internal const ulong RootVtable = 0x13FDA00, MotorVtable = 0x13FD988,
        TitleVtable = 0x13E4E08, GameVtable = 0x13D68C0;
    internal static readonly byte[] ContainsCode = Convert.FromHexString("4C8B49204C8BDA4C8B41104F8D04C14D3BC8743C4C8B41104F8D14C10F1F4000498B014D8BC34883C0704C2BC00F1F000FB610420FB60C002BD1750748FFC085C975ED85D2740C4983C1084D3BCA75D033C0C3B801000000C3");
    internal static readonly GameSessionLayout Original = new(ManagerRva, 0x11154, "488D05550FB5014883C4305BC3",
        0x56FE90, RootVtable, MotorVtable, TitleVtable, GameVtable);
    internal static byte[] GetterCode => Original.GetterCode;

    internal static GameSessionObservation Read(Func<ulong, int, byte[]> read, ulong module, GameSessionLayout? layout = null)
    {
        layout ??= Original;
        try
        {
            byte[] getterCode = layout.GetterCode;
            if (!read(module + layout.Getter, getterCode.Length).AsSpan().SequenceEqual(getterCode)
                || !read(module + layout.Contains, ContainsCode.Length).AsSpan().SequenceEqual(ContainsCode))
                return new(GameSessionPhase.Unknown, 0, 0, [], "State manager code guard mismatch");
            // Never combine a header from one state with objects from another. A race waits
            // for the next poll; it does not reuse the last successful Playing result.
            var first = Sample(read, module, layout);
            var second = Sample(read, module, layout);
            if (first.Stack != second.Stack || first.Pending != second.Pending || first.Phase != second.Phase
                || !first.Entries.SequenceEqual(second.Entries))
                return second with { Phase = GameSessionPhase.Transition, Error = "Active state stack changed during read" };
            return second;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidDataException or ArgumentException or OverflowException)
        { return new(GameSessionPhase.Unknown, 0, 0, [], error.Message); }
    }

    private static GameSessionObservation Sample(Func<ulong, int, byte[]> read, ulong module, GameSessionLayout layout)
    {
        byte[] header = read(module + layout.Manager + 0x10, 0x28);
        ulong count = U64(header, 0), capacity = U64(header, 8), stack = U64(header, 0x10), pending = U64(header, 0x20);
        if (count > capacity || count > 64 || (count > 0 && stack == 0))
            throw new InvalidDataException("Invalid active state stack bounds");
        byte[] pointers = count == 0 ? [] : read(stack, checked((int)count * 8));
        var entries = new GameSessionEntry[(int)count];
        for (int i = 0; i < entries.Length; i++)
        {
            ulong address = U64(pointers, i * 8);
            if (address == 0) throw new InvalidDataException("Null active state object");
            byte[] data = read(address, 0x90);
            ulong vtable = U64(data, 0);
            if (vtable < module) throw new InvalidDataException("Invalid active state object");
            entries[i] = new(address, vtable - module, ReadName(read, address, data),
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x68)));
        }
        if (!header.AsSpan().SequenceEqual(read(module + layout.Manager + 0x10, 0x28))
            || (count > 0 && !pointers.AsSpan().SequenceEqual(read(stack, pointers.Length))))
            return new(GameSessionPhase.Transition, stack, pending, entries, "Active state stack changed during read");
        var phase = Classify(entries, pending, layout);
        return new(phase, stack, pending, entries);
    }

    private static string ReadName(Func<ulong, int, byte[]> read, ulong address, byte[] data)
    {
        // IGameState's name occupies +0x70..+0x46F. Some native child states exceed
        // 32 bytes (e.g. CStrategySceneryInvestSceneryState); never truncate them.
        var name = new StringBuilder();
        for (int offset = 0; offset < 0x400; offset += 0x20)
        {
            ReadOnlySpan<byte> part = offset == 0 ? data.AsSpan(0x70, 0x20)
                : read(checked(address + 0x70 + (ulong)offset), 0x20);
            int end = part.IndexOf((byte)0);
            name.Append(Encoding.ASCII.GetString(end >= 0 ? part[..end] : part));
            if (end >= 0) return name.ToString();
        }
        throw new InvalidDataException("Unterminated active state name");
    }

    internal static GameSessionPhase Classify(IReadOnlyList<GameSessionEntry> entries, ulong pending, GameSessionLayout? layout = null)
    {
        layout ??= Original;
        if (pending != 0 || entries.Any(e => e.Leaving != 0)) return GameSessionPhase.Transition;
        if (entries.Count == 0) return GameSessionPhase.Starting;
        if (entries[0] is not { Name: "CRootState" } root || root.VtableRva != layout.Root) return GameSessionPhase.Unknown;
        if (entries.Count < 3) return GameSessionPhase.Starting;
        if (entries[1] is not { Name: "CMotorGameState" } motor || motor.VtableRva != layout.Motor) return GameSessionPhase.Unknown;
        if (entries.Any(e => e.Name is "CLoadState" or "CGameStartInitState")) return GameSessionPhase.Transition;
        // CTitleState stays below its menu dialogs. CGameState stays below both the
        // user-strategy and date-progress states, including genuine AUTO play.
        return entries[2] switch
        {
            { Name: "CTitleState" } title when title.VtableRva == layout.Title => GameSessionPhase.Title,
            { Name: "CGameState" } game when game.VtableRva == layout.Game => GameSessionPhase.Playing,
            _ => GameSessionPhase.Unknown
        };
    }

    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
}
