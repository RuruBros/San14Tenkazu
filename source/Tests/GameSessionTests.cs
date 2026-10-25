using System.Buffers.Binary;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class GameSessionTests
{
    private sealed class Capture
    {
        internal readonly ulong Module, Stack = 0x70000000;
        internal readonly Dictionary<ulong, byte[]> Memory = [];
        internal readonly byte[] Header;
        internal Capture(string name)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", name)));
            var root = json.RootElement;
            Module = Hex(root.GetProperty("module").GetString()!);
            var rows = root.GetProperty("stack").EnumerateArray().ToArray();
            Header = new byte[0x28];
            Put(Header, 0, (ulong)rows.Length); Put(Header, 8, root.GetProperty("capacity").GetUInt64());
            Put(Header, 0x10, Stack); Put(Header, 0x20, root.GetProperty("pending").GetUInt64());
            Memory[Module + GameSessionReader.ManagerRva + 0x10] = Header;
            Memory[Module + 0x11154] = (byte[])GameSessionReader.GetterCode.Clone();
            Memory[Module + 0x56FE90] = (byte[])GameSessionReader.ContainsCode.Clone();
            var pointers = new byte[rows.Length * 8];
            for (int i = 0; i < rows.Length; i++)
            {
                ulong address = Hex(rows[i].GetProperty("address").GetString()!);
                Put(pointers, i * 8, address);
                Memory[address] = Convert.FromHexString(rows[i].GetProperty("raw").GetString()!);
            }
            Memory[Stack] = pointers;
        }
        internal byte[] Read(ulong address, int size)
        {
            foreach (var (start, bytes) in Memory)
                if (address >= start && address - start <= (ulong)bytes.Length
                    && (ulong)size <= (ulong)bytes.Length - (address - start))
                    return bytes.AsSpan((int)(address - start), size).ToArray();
            throw new IOException("Unreadable state object");
        }
        internal GameSessionObservation Observe() => GameSessionReader.Read(Read, Module);
    }

    private static ulong Hex(string text) => Convert.ToUInt64(text[2..], 16);
    private static void Put(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);

    [Theory]
    [InlineData("GameSessionTitle.json", (int)GameSessionPhase.Title)]
    [InlineData("GameSessionPlaying.json", (int)GameSessionPhase.Playing)]
    public void RecordedLiveStackDistinguishesTitleAndPlay(string file, int expected)
    {
        var capture = new Capture(file);
        Assert.Equal((GameSessionPhase)expected, capture.Observe().Phase);
    }

    [Fact]
    public void TitleWithZeroPlayerAndNoDemoFlagIsNotAuto()
    {
        var data = new TestGame().Build(playerForce: 0);
        typeof(GameSnapshot).GetProperty(nameof(GameSnapshot.Session), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(data, new Capture("GameSessionTitle.json").Observe());
        Assert.False(data.IsDemoPlay);
        Assert.False(data.With([]).IsDemoPlay);
    }

    [Theory]
    [InlineData(true, 10)] [InlineData(false, 0)] [InlineData(false, 10)]
    public void AutoRequiresConfirmedPlayingSession(bool demo, int player)
    {
        var data = new TestGame().Build(playerForce: player);
        typeof(GameSnapshot).GetProperty(nameof(GameSnapshot.DemoFlag), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(data, demo);
        Assert.Equal(demo || player == 0, data.IsDemoPlay);
        typeof(GameSnapshot).GetProperty(nameof(GameSnapshot.Session), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(data, null);
        Assert.False(data.IsDemoPlay);
    }

    [Fact]
    public void PendingTransitionOrLeavingGameCannotReadAsPlaying()
    {
        var capture = new Capture("GameSessionPlaying.json");
        Put(capture.Header, 0x20, 1);
        Assert.Equal(GameSessionPhase.Transition, capture.Observe().Phase);
        Put(capture.Header, 0x20, 0);
        ulong game = BinaryPrimitives.ReadUInt64LittleEndian(capture.Memory[capture.Stack].AsSpan(16));
        capture.Memory[game][0x68] = 1;
        Assert.Equal(GameSessionPhase.Transition, capture.Observe().Phase);
    }

    [Theory]
    [InlineData("CLoadState", (int)GameSessionPhase.Transition)]
    [InlineData("CGameStartInitState", (int)GameSessionPhase.Transition)]
    [InlineData("CProgressState", (int)GameSessionPhase.Playing)]
    [InlineData("CSaveLoadState", (int)GameSessionPhase.Playing)]
    public void ActiveGameSurvivesDialogsButNotTheActualLoadOrInitialization(string name, int expected)
    {
        var observed = new Capture("GameSessionPlaying.json").Observe();
        Assert.Equal((GameSessionPhase)expected, GameSessionReader.Classify(
            observed.Entries.Append(new(0x90000000, 0, name, 0)).ToArray(), 0));
        var title = new Capture("GameSessionTitle.json").Observe();
        Assert.Equal(GameSessionPhase.Title, GameSessionReader.Classify(
            title.Entries.Append(new(0x90000000, 0, "CSaveLoadState", 0)).ToArray(), 0));
    }

    [Theory]
    [InlineData("bounds")] [InlineData("code")] [InlineData("object")] [InlineData("name")]
    public void InvalidOrUnknownStateNeverFallsBackToTables(string kind)
    {
        var capture = new Capture("GameSessionPlaying.json");
        ulong game = BinaryPrimitives.ReadUInt64LittleEndian(capture.Memory[capture.Stack].AsSpan(16));
        if (kind == "bounds") Put(capture.Header, 0, 65);
        if (kind == "code") capture.Memory[capture.Module + 0x11154][0] ^= 1;
        if (kind == "object") capture.Memory.Remove(game);
        if (kind == "name") capture.Memory[game][0x70] = (byte)'X';
        Assert.Equal(GameSessionPhase.Unknown, capture.Observe().Phase);
    }

    [Fact]
    public void LongNativeChildStateNameIsReadWithoutTruncation()
    {
        const string name = "CStrategySceneryInvestSceneryState";
        var capture = new Capture("GameSessionPlaying.json");
        ulong child = BinaryPrimitives.ReadUInt64LittleEndian(capture.Memory[capture.Stack].AsSpan(32));
        var data = new byte[0x470];
        capture.Memory[child].AsSpan(0, 0x70).CopyTo(data);
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(data, 0x70);
        capture.Memory[child] = data;
        var observed = capture.Observe();
        Assert.Equal(GameSessionPhase.Playing, observed.Phase);
        Assert.Equal(name, observed.Entries[^1].Name);
        data.AsSpan(0x70).Fill((byte)'X');
        Assert.Equal(GameSessionPhase.Unknown, capture.Observe().Phase);
    }

    [Fact]
    public void StackChangeBetweenReadsCannotPublishPlayingSnapshot()
    {
        var capture = new Capture("GameSessionPlaying.json");
        int headers = 0;
        byte[] Read(ulong address, int length)
        {
            if (address == capture.Module + GameSessionReader.ManagerRva + 0x10 && ++headers == 3)
                Put(capture.Header, 0x20, 1);
            return capture.Read(address, length);
        }
        Assert.Equal(GameSessionPhase.Transition, GameSessionReader.Read(Read, capture.Module).Phase);
    }
}
