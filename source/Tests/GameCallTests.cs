using System.Text.Json;

namespace San14Tenkazu.Tests;

// The stubs that run one game function on its frame (GameMemory.Calls.cs). These bytes were run in an x64 emulator
// (unicorn, 2026-10-03; Reverse/stub.py runs a stub so again) for the addresses below: the first entry calls the
// function once with the arguments given and rsp 16-byte aligned at the call, keeps its eax at StubResult, marks Done
// and lands on Update with rsp, rcx, rdx, r8, r9 and xmm0~3 as they came in; later entries go straight on to Update.
// The guarded one, with the player force byte 0 or no base or state object, marks Skipped without calling. A change to
// the stub must be checked that way again before this test is changed.
public sealed class GameCallTests
{
    private const ulong Code = 0x2_0000_0000, Data = Code + 0x1000;
    private const ulong BasePointer = 0x7FF6_0277_DF18, Switch = 0x7FF6_0033_A630, Surrender = 0x7FF6_003C_1A00, Camera = 0x7FF6_0045_1360;

    private const string Guarded =
        "803DF90F0000007406FF25F90F0000C605EA0F00000148B818DF7702F67F0000488B004885C07412488B80E87108004885C0740680783A00" +
        "750DC605BF0F000003FF25C10F00005152415041514883EC68F30F7F442420F30F7F4C2430F30F7F542440F30F7F5C245048B90000000000" +
        "00000048BA000000000000000048B830A63300F67F0000FFD08905750F0000F30F6F442420F30F6F4C2430F30F6F542440F30F6F5C245048" +
        "83C468415941585A59C605480F000002FF254A0F0000";

    private const string Plain =
        "803DF90F0000007406FF25F90F0000C605EA0F0000015152415041514883EC68F30F7F442420F30F7F4C2430F30F7F542440F30F7F5C2450" +
        "48B9000000000000000048BA401000000200000048B8001A3C00F67F0000FFD08905A60F0000F30F6F442420F30F6F4C2430F30F6F542440" +
        "F30F6F5C24504883C468415941585A59C605790F000002FF257B0F0000";

    private const string WithMore =
        "803DF90F0000007406FF25F90F0000C605EA0F0000015152415041514883EC78F30F7F442430F30F7F4C2440F30F7F542450F30F7F5C2460" +
        "48B8000000000000F0BF488944242048B80100000000000000488944242849B8F40100000000000066490F6ED049B9000000000000F0BF66" +
        "490F6ED948B9393000000000000048BA000000000000000048B860134500F67F0000FFD089056A0F0000F30F6F442430F30F6F4C2440F30F" +
        "6F542450F30F6F5C24604883C478415941585A59C6053D0F000002FF253F0F0000";

    [Fact]
    public void GuardedStubIsTheOneCheckedInTheEmulator()
    {
        byte[] stub = GameMemory.FrameStub(Code, Data, Switch, 0, 0, BasePointer);

        Assert.Equal(Guarded, Convert.ToHexString(stub));
    }

    [Fact]
    public void PlainStubIsTheOneCheckedInTheEmulator()
    {
        byte[] stub = GameMemory.FrameStub(Code, Data, Surrender, 0, Data + GameMemory.StubArgs, 0);

        Assert.Equal(Plain, Convert.ToHexString(stub));
    }

    // The camera's flight (hex 12345, 0, 500 ms, -1.0, -1.0, 1), run in the emulator by Reverse/stub.py on 2026-10-03:
    // the function saw rcx, rdx, r8 and xmm2 = 500, r9 and xmm3 = -1.0, and on the stack after the shadow space -1.0
    // and 1, with rsp 16-byte aligned at the call; all else as for the plain one.
    [Fact]
    public void StubWithMoreArgumentsIsTheOneCheckedInTheEmulator()
    {
        ulong keep = (ulong)BitConverter.DoubleToInt64Bits(-1.0);
        byte[] stub = GameMemory.FrameStub(Code, Data, Camera, 12345, 0, 0, [GameMemory.CameraFlight, keep, keep, 1]);

        Assert.Equal(WithMore, Convert.ToHexString(stub));
    }

    // Without more arguments the stub is the plain one.
    [Fact]
    public void NoMoreArgumentsLeaveTheStubAsItWas()
    {
        Assert.Equal(Plain, Convert.ToHexString(GameMemory.FrameStub(Code, Data, Surrender, 0, Data + GameMemory.StubArgs, 0, [])));
    }

    // These sequences were executed by alliance_stub_check.py, including deliberately clobbered volatile
    // registers, two entries, aligned calls and exact flag stores. Keep the bytes tied to that emulator evidence.
    [Fact]
    public void AllianceFrameSequencesMatchTheEmulatorVerifiedBytes()
    {
        using JsonDocument input = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "force_alliance_stubs.json")));
        foreach (JsonElement c in input.RootElement.EnumerateArray())
        {
            GameMemory.FrameInvocation Call(JsonElement invocation)
            {
                ulong[] args = invocation.GetProperty("args").EnumerateArray().Select(a => a.GetUInt64()).ToArray();
                return new(invocation.GetProperty("function").GetUInt64(), args[0], args[1], args.Skip(2).ToArray());
            }
            GameMemory.FrameInvocation[] calls = c.GetProperty("calls").EnumerateArray().Select(Call).ToArray();
            GameMemory.FrameStore[] stores = c.GetProperty("stores").EnumerateArray().Select(s =>
                new GameMemory.FrameStore(s.GetProperty("Address").GetUInt64(), s.GetProperty("Value").GetByte())).ToArray();
            Assert.Equal(c.GetProperty("hex").GetString(), Convert.ToHexString(GameMemory.FrameStub(
                c.GetProperty("code").GetUInt64(), c.GetProperty("data").GetUInt64(), calls[0].Function,
                calls[0].Rcx, calls[0].Rdx, 0, calls[0].More, calls.Skip(1).ToArray(), stores)));
        }
    }

    // The game's list of held 명품 holds exactly those whose state is 1: a 명품 given from 미발견 goes on, one taken
    // back to 미발견 comes off, and one that moves between holders stays as it is.
    [Theory]
    [InlineData(ItemFields.Found, false, true)]
    [InlineData(ItemFields.Hidden, true, false)]
    [InlineData(3, true, false)]
    [InlineData(ItemFields.Found, true, null)]
    [InlineData(ItemFields.Hidden, false, null)]
    public void OwnedListHoldsTheHeldItems(int state, bool listed, bool? add)
    {
        Assert.Equal(add, GameMemory.OwnedListFix(state, listed));
    }
}
