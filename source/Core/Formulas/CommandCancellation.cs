using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// The refund/request and removal stages of 0x334780, composed by ReturnCommand.Cancel with return construction
// and command storage. Graphical host objects remain host boundaries.
internal static class CommandCancellation
{
    // Dispatch RVAs are input metadata verified from the command's native vtable, not predicted outcomes.
    internal sealed record Command(int Kind, Rec Data, IReadOnlyList<int> Officers,
        int PhaseGetter, int CancelHandler, int ReturnHandler, int RemovalHandler,
        CommandTravel.Dispatch? Travel = null, IReadOnlyList<CommandTravel.Destination>? Destinations = null)
    {
        // Native membership is mutable while the command object, ID and storage aliases remain the same.
        public IReadOnlyList<int> Officers { get; internal set; } = Officers;
    }
    internal sealed record ReturnRequest(Command? Source, IReadOnlyList<Rec> Officers, bool Notify);
    internal readonly record struct RemovalRequest(Command Source, bool Immediate);

    internal static void RequireSupported(World w, Command command)
    {
        int expected = command.Kind switch
        {
            1 => 0x1F1F80,
            4 or 5 or 6 or 7 or 8 or 9 or 19 => 0x1F1B30,
            10 or 11 or 12 or 13 or 14 or 20 or 24 => 0x1F1FC0,
            2 or 3 or 15 or 16 or 21 or 25 => 0x1F1F70,
            _ => 0,
        };
        // 0x14D350 returns literal zero: phase-1/2/3 branches cannot occur for these classes.
        if (expected == 0 || command.CancelHandler != expected || command.PhaseGetter != 0x14D350 ||
            command.ReturnHandler != 0x235620 || command.RemovalHandler != 0x2289D0)
            throw new NotSupportedException("This command dispatch has not been verified.");
        if (command.CancelHandler == 0x1F1B30 && !Objects.ForceValid(w.Entry(Tables.Forces, command.Data.U8(0x75), 51)) &&
            Objects.ItemValid(w.Entry(Tables.Items, command.Data.U16(0x76), 201)))
            CommandTravel.RequireDispatch(command);
    }

    internal static ReturnRequest RefundAndRequestReturn(World w, Command command)
    {
        RequireSupported(w, command);
        Rec[] officers = command.Officers.Select(id => w.OfficerById(id) ??
            throw new NotSupportedException("A command references a missing native officer pointer.")).ToArray();
        Rec data = command.Data;
        if (command.CancelHandler == 0x1F1FC0 && data.I32(0x78) > 0 && officers.Length != 0)
        {
            Rec first = officers[0], force = Objects.ForceOf(w, first);
            // Person_GetForce returns force 0 for an invalid corps, so the native fallback main-corps
            // refund cannot be reached through this input path when the force validity check succeeds.
            if (Objects.ForceValid(force))
            {
                Rec corps = w.Entry(Tables.Corps, first.U8(0xE0), 51);
                int maximum = CorpsLifecycle.MaximumBudget(w, corps);
                int value = Math.Min(unchecked(data.I32(0x78) + corps.I32(0x24)), maximum);
                BinaryPrimitives.WriteInt32LittleEndian(corps.Bytes.AsSpan(0x24), value < 0 ? 0 : Math.Min(value, maximum));
            }
        }
        if (command.CancelHandler == 0x1F1B30)
        {
            Rec force = w.Entry(Tables.Forces, data.U8(0x75), 51);
            if (Objects.ForceValid(force))
            {
                // 0x22E8C0 reads the raw ruler ID for the base; 0x22E970 uses the force index for ownership.
                Rec ruler = w.OfficerById(force.U16(0x10))!;
                Rec? home = Map.Base(w, ruler.U16(0xE2));
                if (Objects.Valid(w, home))
                {
                    Rec item = w.Entry(Tables.Items, data.U16(0x76), 201);
                    if (Objects.ItemValid(item))
                    {
                        item.Bytes[0x4F] &= 0xF7;
                        int owner = force.Index != 0 ? force.U16(0x10) : 0x1396;
                        // An already-valid item has state 1,
                        // so 0x323ED0 does not append it to the owned-item list again.
                        if ((uint)(owner - 1) < 6000)
                            BinaryPrimitives.WriteUInt16LittleEndian(item.Bytes.AsSpan(0x4A), (ushort)owner);
                    }
                    Engagement.AddBaseResources(w, home!, data.I32(0x78), data.I32(0x7C));
                }
            }
            else
            {
                Rec item = w.Entry(Tables.Items, data.U16(0x76), 201);
                if (Objects.ItemValid(item))
                {
                    int county = CommandTravel.Location(w, command).U16(0x12);
                    if (officers.Length != 0 && Objects.PersonValid(officers[0]))
                    {
                        Rec? home = Map.Base(w, officers[0].U16(0xE2));
                        if (Objects.Valid(w, home) && home!.U16(0x4E) < 52 &&
                            !w.List(Lists.CountyList).Any(n => n[0] is { } c && (uint)(home.Index - 1) < 51 && c.U8(0x35) == home.Index))
                            // 0x1F1EBE reads a null list head after shuffling the empty list, without consuming RNG.
                            throw new InvalidOperationException("Native orphan refund reads a null county head before changing the item.");
                    }
                    // With a nonempty home-county list the native code keeps the current tile's county.
                    item.Bytes[0x4F] &= 0xF7;
                    if ((uint)(county - 1) < 500)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(item.Bytes.AsSpan(0x4A), 0);
                        BinaryPrimitives.WriteUInt16LittleEndian(item.Bytes.AsSpan(0x4C), (ushort)county);
                        item.Bytes[0x50] = 2;
                        w.List(Lists.OwnedItems).RemoveAll(n => ReferenceEquals(n[0], item));
                    }
                }
            }
        }
        // 0x224670 filters status 1..4 while preserving source order and repeated officer IDs.
        return new(command, officers.Where(p => p.U8(0xE6) is >= 1 and <= 4).ToArray(), data.I32(0x50) != 0);
    }

    // 0x2289D0 -> 0x32A5E0: any nonzero removal flag suppresses the storage call. The caller passes 1,
    // which deletes immediately in 0x1F22C0 rather than placing the command in the pending-delete list.
    internal static RemovalRequest? RequestRemoval(Command command)
    {
        if (command.RemovalHandler != 0x2289D0)
            throw new NotSupportedException("This command removal handler has not been verified.");
        if (command.Data.I32(0x54) != 0) return null;
        BinaryPrimitives.WriteInt32LittleEndian(command.Data.Bytes.AsSpan(0x54), 1);
        return new(command, true);
    }
}
