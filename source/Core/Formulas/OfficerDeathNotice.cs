using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal sealed record OfficerDeathPresentation(Action Reset, Action<int, int> BindForce,
    Action<int, int> BindOfficer, Action<string, int> BindText, Action<int, int> BindNumber,
    Func<Rec, string> OfficeName, Func<int, string> Message, Action<int> Sound, Action<string> Show);

// 0x295AA0: ordinary player notices and history precede appointment removal and slot reset.
// Localization and physical presentation are host requests; record writes remain in the core.
internal sealed class OfficerDeathNotice(World w, HistoryLog history, OfficerDeathPresentation presentation,
    bool playbackIdle, bool playerSlotsRefreshAvailable, Action refreshPlayerSlots)
{
    internal Rec Prepare(Rec person, Rec force, Rec office, int mode, bool includeHistory)
    {
        if (mode is < 0 or > 2 || person.U8(0xE6) is not (3 or 4) || !Objects.IsPlayerForce(w, force) ||
            office.U8(0x37) < OfficerDeath.NoticeOfficeGrade)
            throw new NotSupportedException("This notice requires an ordinary player officer without office cutscenes.");
        int count = w.WorldState.U8(0x165D);
        if (0x1668 + count * 4 > w.WorldState.Bytes.Length)
            throw new NotSupportedException("Player slot timers extend beyond the copied world state.");
        Rec? ruler = w.OfficerById(force.U16(0x10));
        if (includeHistory && playbackIdle && Objects.Valid(w, ruler) &&
            (OfficerRelations.Sworn(w, ruler, person) || OfficerRelations.Spouse(w, ruler, person) ||
             OfficerRelations.Related(w, ruler, person) || OfficerRelations.Likes(w, ruler!, person)))
            throw new NotSupportedException("A related player ruler requires the separate playback event body.");
        if (!includeHistory) return person;
        Rec corps = CorpsLifecycle.MainCorps(w, force);
        // 0x2B4110 returns the valid adviser before building or sorting fallback candidates.
        if (!Objects.CorpsValid(corps) || corps.U8(0x10) != force.Index)
            throw new NotSupportedException("The player narrator requires a valid player main corps.");
        Rec? adviser = w.OfficerById(force.U16(0x52));
        if (!Objects.Valid(w, adviser))
            throw new NotSupportedException("The narrator fallback candidate body has not been verified.");
        return adviser!;
    }

    internal void Emit(Rec person, Rec force, Rec office, Rec location, Rec narrator, int mode, bool includeHistory)
    {
        presentation.Sound(0x16);
        presentation.Reset();
        presentation.BindForce(force.Index, 0);
        presentation.BindText(Objects.Valid(w, office) && person.U8(0xE7) != 0x49 ? presentation.OfficeName(office) : "", 0);
        presentation.BindOfficer(person.U16(0x10), 0);
        presentation.BindNumber(w.WorldState.I16(0x34), 0);
        presentation.BindNumber(w.WorldState.I8(0x36), 1);
        presentation.Show(presentation.Message(0x2A2D));
        if (!includeHistory) return;
        presentation.Reset();
        presentation.BindOfficer(person.U16(0x10), 0);
        string primary = presentation.Message(mode switch { 1 => 0x2A30, 2 => 0x2A31, _ => 0x2A2E });
        presentation.Reset();
        presentation.BindOfficer(narrator.U16(0x10) == person.U16(0x10) ? 5012 : narrator.U16(0x10), 0);
        presentation.BindOfficer(force.U16(0x10), 1);
        presentation.BindOfficer(person.U16(0x10), 3);
        string secondary = presentation.Message(mode switch { 1 => 0x52AB, 2 => 0x52C6, _ => 0x528F });
        int speaker = narrator.U16(0x10) == person.U16(0x10) ? 5012 : narrator.U16(0x10);
        history.Add(new(1, CorpsLifecycle.MainCorps(w, force).Index, location.Index, 0,
            w.WorldState.I32(0x450), primary, secondary, speaker, 1, 1));
    }

    // 0x29C870 clears every matching alias, then requests a refresh for each cleared slot.
    internal void ClearPlayerSlots(Rec person)
    {
        for (int i = 0; i < w.WorldState.U8(0x165D); i++)
        {
            Rec? member = w.OfficerById(w.WorldState.U16(0x165E + i * 2));
            if (!Objects.Valid(w, member) || person.U16(0x10) != member!.U16(0x10)) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(w.WorldState.Bytes.AsSpan(0x165E + i * 2), 0);
            w.WorldState.Bytes.AsSpan(0x1668 + i * 4, 4).Clear();
            if (playerSlotsRefreshAvailable) refreshPlayerSlots();
        }
    }
}
