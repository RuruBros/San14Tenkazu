using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class OfficerSelection
{
    // 0x29C870: retain slot count/order, clear each valid matching ID and date, then query and refresh the UI.
    // The host owns CGameState lookup, panel availability and the graphical 0x897530 body.
    internal static void Remove(World w, Rec? person, Action queryAndRefreshUi)
    {
        if (!Objects.Valid(w, person)) return;
        Rec state = w.WorldState;
        for (int i = 0; i < state.U8(0x165D); i++)
        {
            Rec? selected = w.OfficerById(state.U16(0x165E + i * 2));
            if (!Objects.Valid(w, selected) || selected!.U16(0x10) != person!.U16(0x10)) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(state.Bytes.AsSpan(0x165E + i * 2), 0);
            BinaryPrimitives.WriteInt32LittleEndian(state.Bytes.AsSpan(0x1668 + i * 4), 0);
            queryAndRefreshUi();
        }
    }
}
