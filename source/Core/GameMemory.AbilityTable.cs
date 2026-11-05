using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    // The native getter returns a manager whose +0x60 holds the indexed level records.
    // Only the ordinary table is certified here. Runtime cap and trait patches are independent.
    internal static bool ReadOriginalAbilityTable(Func<ulong, int, byte[]> read, ulong module, GameAbilityTable profile,
        RuntimeRuleBuildProfile? runtime = null, RuntimeRuleSettings? rules = null)
    {
        const int rows = 101, pointerBytes = rows * sizeof(ulong);
        ulong managerSlot = checked(module + profile.ManagerPointerRva);
        try
        {
            foreach (var guard in profile.Guards)
            {
                var bytes = read(checked(module + guard.Rva), guard.Length);
                if (bytes.Length != guard.Length ||
                    runtime is not null && rules is not null && !RuntimeRulePatches.Normalize(guard.Rva, bytes, runtime, rules) ||
                    Convert.ToHexString(SHA256.HashData(bytes)) != guard.Sha256)
                    return false;
            }
            ulong manager = BinaryPrimitives.ReadUInt64LittleEndian(read(managerSlot, sizeof(ulong)));
            if (manager == 0) return false;
            ulong table = checked(manager + 0x60);
            var pointers = read(table, pointerBytes);
            if (pointers.Length != pointerBytes) return false;
            ulong recordClass = checked(module + profile.RecordClassRva);
            for (int level = 0; level < rows; level++)
            {
                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(pointers.AsSpan(level * sizeof(ulong)));
                if (address == 0) return false;
                var record = read(address, 0x24);
                if (record.Length != 0x24 || BinaryPrimitives.ReadUInt64LittleEndian(record) != recordClass) return false;
                for (int ability = 0; ability < 5; ability++)
                    if (BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(0x10 + ability * sizeof(int))) !=
                        AbilityProgress.Threshold(level, ability == 4, growth: false))
                        return false;
            }
            return manager == BinaryPrimitives.ReadUInt64LittleEndian(read(managerSlot, sizeof(ulong))) &&
                pointers.AsSpan().SequenceEqual(read(table, pointerBytes));
        }
        catch (Exception error) when (error is Win32Exception or IOException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
