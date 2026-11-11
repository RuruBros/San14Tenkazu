#if LOCAL_DEVELOPER
using System.IO;
using San14Tenkazu;
using San14Tenkazu.Tests;

internal static partial class RenderChecks
{
    private static void CheckHeldProvocation()
    {
        var data = EditorCoverageFixture.Snapshot();
        var unit = data.Units.Single();
        var duration = UnitFields.Ailments[UnitFields.Provoked];
        var kind = new FieldDef("kind", RecordKind.Unit, 0x3C, FieldKind.Byte, 0, 255);
        var number = new FieldDef("number", RecordKind.Unit, 0x3E, FieldKind.UInt16, 0, ushort.MaxValue);
        data = data.With([new(RecordKind.Unit, unit.Address, unit.Data, "fixture", [new(duration, 0), new(kind, 255), new(number, 0)])]);
        unit = data.Units.Single();
        var heldData = data.With([new(RecordKind.Unit, unit.Address, unit.Data, "fixture", [new(duration, 12), new(kind, 27), new(number, 300)])]);
        var held = heldData.Units.Single().Data;
        var plan = new GameMemory.ProvocationPlan(1, 0, DateTime.UnixEpoch, GameNativeFactsNovember2026.ExeSha256,
            0, 188, 12, 11, 0, 1, unit.Commander, unit.Slot, unit.Address, 6, 300, unit.Address + 0x10000,
            Convert.ToHexString(unit.Data), "source", Convert.ToHexString(unit.Data), Convert.ToHexString(held), 0, 0, 0, 0,
            [new(0x1000, "90")], [new(unit.Address, "00")], true, false);
        foreach (var invalid in new[] { plan with { ImmunityAll = 1 }, plan with { SourceSlot = 301 }, plan with { LiveNativeCall = true } })
        {
            bool refused = false;
            try { GameMemory.ValidateProvocationPlan(invalid); } catch (InvalidDataException) { refused = true; }
            Require(refused, "Invalid Native admission/target/provenance must be rejected");
        }
        var memory = new FakeMemory(data);
        GameMemory.ApplyProvocationBytes(memory, TestGame.Classes, plan, _ => { });
        Require(memory.Read(unit.Address, unit.Data.Length).AsSpan().SequenceEqual(held), "Held state must match the entire Native-generated unit");
        var decrease = UnitEdits.Plan(heldData, unit.Slot, unit.Commander, [new(duration, 11)]);
        Require(!decrease.Refused, "Held decrease must use the real unit planner");
        GameMemory.Apply(memory, TestGame.Classes, decrease.Edits);
        Require(duration.Read(memory.Read(unit.Address, unit.Data.Length)) == 11, "Product decrease must reach memory");
        GameMemory.Apply(memory, TestGame.Classes, Undo.Of(decrease.Edits));
        GameMemory.RestoreProvocationBytes(memory, TestGame.Classes, plan);
        Require(memory.Read(unit.Address, unit.Data.Length).AsSpan().SequenceEqual(unit.Data), "Held duration, two-byte target and neighbors must restore");
        foreach (bool partial in new[] { false, true })
        {
            var failing = new HeldFailureMemory(new FakeMemory(data), partial);
            bool observed = false;
            try { GameMemory.ApplyProvocationBytes(failing, TestGame.Classes, plan,
                _ => { if (!partial && failing.Writes >= 3) throw new System.IO.IOException("injected held log failure"); }); }
            catch (System.IO.IOException) { observed = true; }
            Require(observed && failing.Inner.Read(unit.Address, unit.Data.Length).AsSpan().SequenceEqual(unit.Data),
                "Partial target write/log failure must restore the whole original unit");
        }
        GameMemory.ApplyProvocationBytes(memory, TestGame.Classes, plan, _ => { });
        memory.Write(unit.Address + 0x3D, [0xA5]);
        bool rejected = false;
        try { GameMemory.RestoreProvocationBytes(memory, TestGame.Classes, plan); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && memory.Read(unit.Address + 0x3D, 1)[0] == 0xA5, "Recovery must refuse foreign adjacent changes, never overwrite them");
        Console.WriteLine("PASS held provocation: Native admission shape, hold, product decrement/Undo, full restore, partial UInt16 target, log failure, foreign neighbor; gameAccess=false");
    }

    private sealed class HeldFailureMemory(FakeMemory inner, bool partial) : IRecordMemory
    {
        internal readonly FakeMemory Inner = inner;
        internal int Writes;
        public byte[] Read(ulong address, int length) => Inner.Read(address, length);
        public void Write(ulong address, byte[] bytes)
        {
            if (++Writes == 3 && partial) { Inner.Write(address, bytes[..1]); throw new System.IO.IOException("injected partial UInt16 target"); }
            Inner.Write(address, bytes);
        }
    }
}
#endif
