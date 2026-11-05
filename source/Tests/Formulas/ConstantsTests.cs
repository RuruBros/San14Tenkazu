using System.Globalization;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ConstantsTests
{
    // The Steam stub leaves .data readable on disk. Select the verified locations by executable hash, then
    // compare each value with the shared formula constant so relocations and retuning are checked separately.
    [GameExeFact]
    public void MatchTheExe()
    {
        byte[] exe = File.ReadAllBytes(GameFiles.Exe);
        string hash = Convert.ToHexString(SHA256.HashData(exe));
        Dictionary<string, string>? relocated = null;
        var checkedLocations = new HashSet<string>();
        if (hash != GameBuildFacts.ExeSha256)
        {
            using var profile = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vectors", "FormulaRvas20261105.json")));
            Assert.Equal(profile.RootElement.GetProperty("exeSha256").GetString(), hash);
            relocated = profile.RootElement.GetProperty("rvas").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }

        using var stream = new MemoryStream(exe, writable: false);
        using var pe = new PEReader(stream);
        var wrong = new List<string>();
        int n = 0;
        foreach (Type t in typeof(World).Assembly.GetTypes().Where(t => t.Namespace == typeof(World).Namespace))
            foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.GetCustomAttribute<RvaAttribute>() is not { } rva) continue;
                string name = $"{t.Name}.{f.Name}";
                int address = rva.Rva;
                if (relocated is not null)
                {
                    string previous = address.ToString("X", CultureInfo.InvariantCulture);
                    Assert.True(relocated.TryGetValue(previous, out string? entry), $"Missing formula location for {name}");
                    checkedLocations.Add(previous);
                    address = int.Parse(entry!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                int expected = Convert.ToInt32(f.GetRawConstantValue() ?? f.GetValue(null));
                int actual = pe.GetSectionData(address).GetReader(0, sizeof(int)).ReadInt32();
                n++;
                if (actual != expected) wrong.Add($"{name} (rva {address:X}): {expected}, exe {actual}");
            }
        if (relocated is not null) Assert.Equal(relocated.Count, checkedLocations.Count);
        Assert.True(n > 0);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
