using System.Reflection;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ConstantsTests
{
    // Every [Rva] constant of the formulas equals the int the installed exe holds there. The rule block sits in
    // .data, which the Steam stub leaves readable on disk (only .text is encrypted), so a patch that retunes a rule
    // fails here.
    [GameExeFact]
    public void MatchTheExe()
    {
        byte[] exe = File.ReadAllBytes(GameFiles.Exe);
        int pe = BitConverter.ToInt32(exe, 0x3C);
        int sections = BitConverter.ToUInt16(exe, pe + 6), optional = BitConverter.ToUInt16(exe, pe + 20);
        int FileOffset(int rva)
        {
            for (int k = 0; k < sections; k++)
            {
                int o = pe + 24 + optional + k * 40;
                int va = BitConverter.ToInt32(exe, o + 12), raw = BitConverter.ToInt32(exe, o + 16), at = BitConverter.ToInt32(exe, o + 20);
                if (rva >= va && rva < va + raw) return at + rva - va;
            }
            throw new InvalidOperationException($"rva {rva:X} is in no section");
        }

        var wrong = new List<string>();
        int n = 0;
        foreach (Type t in typeof(World).Assembly.GetTypes().Where(t => t.Namespace == typeof(World).Namespace))
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.GetCustomAttribute<RvaAttribute>() is not { } rva) continue;
                int expected = Convert.ToInt32(f.GetRawConstantValue() ?? f.GetValue(null));
                int actual = BitConverter.ToInt32(exe, FileOffset(rva.Rva));
                n++;
                if (actual != expected) wrong.Add($"{t.Name}.{f.Name} (rva {rva.Rva:X}): {expected}, exe {actual}");
            }
        Assert.True(n > 0);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
