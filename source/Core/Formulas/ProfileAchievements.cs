using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Persistent profile rules; wall-clock input and the Steam notification are explicit host boundaries.
internal sealed class ProfileAchievements(World w, Rec profile, IReadOnlyList<uint> excludedScenarioBits,
    bool notificationAvailable, Func<ProfileAchievements.Clock> readClock, Action<int>? notify = null)
{
    internal readonly record struct Clock(int YearSince1900, int MonthSinceJanuary, int Day, int Hour);

    // 0x332D60: scenario flags, challenge state and nine exclusion bits from 0x1A28098.
    internal bool CanRecord()
    {
        if (excludedScenarioBits.Count != 9) throw new InvalidDataException("Nine profile exclusion rules are required.");
        uint flags = w.WorldState.U32(0x16A8);
        if ((flags & 0x100) != 0 || w.Value("challenge_active") != 0 || (flags & 0x78) != 0) return false;
        return !excludedScenarioBits.Any(bit => bit <= 31 && (flags >> (int)bit & 1) != 0);
    }

    // 0x3EF4D0: notify after writing the bit, then stamp a previously unset bit with the host's tm fields.
    internal void RecordBit(int index, int enabled, int silent)
    {
        if ((uint)index >= 57 || !CanRecord()) return;
        if (enabled != 0 && silent == 0 && notificationAvailable && notify is null)
            throw new NotSupportedException("Profile notification requires its host request.");
        int at = 0x34 + (index >> 5) * 4;
        uint mask = 1u << (index & 31), before = profile.U32(at);
        profile.Bytes[0x274] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(profile.Bytes.AsSpan(at), before & ~mask);
        if (enabled == 0) return;
        BinaryPrimitives.WriteUInt32LittleEndian(profile.Bytes.AsSpan(at), before | mask);
        if (silent == 0 && notificationAvailable) notify!(index);
        if ((before & mask) != 0) return;
        Clock time = readClock();
        int date = 0x44 + index * 6;
        BinaryPrimitives.WriteUInt16LittleEndian(profile.Bytes.AsSpan(date), unchecked((ushort)(time.YearSince1900 + 1900)));
        profile.Bytes[date + 2] = unchecked((byte)(time.MonthSinceJanuary + 1));
        profile.Bytes[date + 3] = unchecked((byte)time.Day);
        profile.Bytes[date + 4] = unchecked((byte)time.Hour);
    }

    // 0x3E68B0 and iterator predicate 0x3331B0: count the registered county seat tiles, including aliases.
    internal int Territory(int requireEligibility, int checkAll, int silent)
    {
        if (requireEligibility != 0 && !CanRecord()) return 0;
        Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
        if (!Objects.ForceValid(player)) return 0;
        int count = 0;
        foreach (Rec?[] node in w.List(Lists.CountyList))
        {
            Rec county = node[0] ?? throw new InvalidDataException("The native county iterator dereferences null payloads.");
            if (w.Entry(Tables.Tiles, county.U16(0x3A), 48399).U8(0x14) == player.Index) count++;
        }
        if (count < 200) return 0;
        RecordBit(16, 1, silent);
        if (checkAll == 0) return 1;
        for (int bit = 1; bit <= 37; bit++)
            if ((profile.U32(0x34 + (bit >> 5) * 4) >> (bit & 31) & 1) == 0) return 1;
        RecordBit(0, 1, silent);
        return 1;
    }
}
