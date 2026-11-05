namespace San14Tenkazu;

// A single unit selector's execute method. Native leaves still do all pathfinding and command transitions.
// Windows x64 ABI: preserve RBX/RSI/RDI/R12/R13, aligned stack and 32-byte shadow space at every call.
internal static class AiExtensionStub
{
    internal const int Page = 0x1000, Vtable = 0x108, Installer = 0x800, Config = 32;
    internal const ulong OriginalVtableRva = 0x168FE80;
    internal const ulong SelectorRva = 0xB2BC40, AgentUnitRva = 0xB6070, ValidRva = 0x32F500;
    internal const ulong CorpsIndexRva = 0x22BAE0, DirectCorpsRva = 0x233960;
    internal const ulong RecoverRva = 0x20060, EvadeRva = 0x201E0, RetreatRva = 0x61120, HoldRva = 0x304700;
    internal const ulong WorldPointerRva = GameBuildFacts.Memory.BasePointerRva;
    internal const ulong Magic = 0x3154584549415453; // STAIEXT1

    internal sealed record Sites(ulong Selector, ulong AgentUnit, ulong Valid, ulong CorpsIndex, ulong DirectCorps,
        ulong Recover, ulong Evade, ulong Retreat, ulong Hold, ulong World);
    internal static readonly Sites Original = new(SelectorRva, AgentUnitRva, ValidRva, CorpsIndexRva, DirectCorpsRva,
        RecoverRva, EvadeRva, RetreatRva, HoldRva, WorldPointerRva);
    internal static readonly Sites November = new(0xB2E510, 0xB7100, 0x331780, 0x22D520, 0x235300,
        0x20040, 0x201C0, 0x61C90, 0x306AF0, GameNativeFactsNovember2026.Memory.BasePointerRva);

    internal static byte[] Build(ulong exe, ulong data, Sites? sites = null)
    {
        sites ??= Original;
        var a = new Assembler();
        a.Hex("535657415441554883EC304C89442420488BD9488BF2498BF8"); // save; +0x20 agent, node, context
        a.Immediate("49BD", data); // r13 = immutable header / aligned config and counters
        a.Hex("F049FF4570"); // diagnostic entries, including disabled/excluded units
        a.Hex("41F6452001"); a.Jump("84", "original");
        a.Hex("488BCE488BD7"); a.Call(exe + sites.AgentUnit); a.Hex("4C8BE0488BC8"); a.Call(exe + sites.Valid);
        a.Hex("85C0"); a.Jump("84", "original");
        a.Hex("41807C241001"); a.Jump("85", "original"); // combat only
        a.Hex("41F644242602"); a.Jump("85", "original"); // supply connected
        a.Hex("4C89E1"); a.Call(exe + sites.CorpsIndex);
        a.Hex("85C0"); a.Jump("84", "original");
        a.Hex("83F833"); a.Jump("87", "original");
        a.Immediate("48BA", exe + sites.World); a.Hex("488B124885D2"); a.Jump("84", "original");
        a.Hex("488B8CC2D0E00000");
        a.Hex("488BF9488BCF"); a.Call(exe + sites.Valid); a.Hex("85C0"); a.Jump("84", "original");
        a.Immediate("48BA", exe + sites.World); a.Hex("488B12488B92E87108004885D2"); a.Jump("84", "original");
        a.Hex("0FB6471085C0"); a.Jump("84", "original");
        a.Hex("3A423A"); a.Jump("85", "controlled"); // another force
        a.Hex("41F6452004"); a.Jump("84", "original");
        a.Hex("488BCF"); a.Call(exe + sites.DirectCorps); a.Hex("85C0"); a.Jump("85", "original");
        a.Label("controlled");
        // RDI was needed for the corps; the unit agent itself is R12 (AgentType=1, B6070).
        a.Hex("498B4D30488BD64D8BC4"); a.Call(exe + sites.Evade);
        a.Hex("83F802"); a.Jump("85", "done"); // preserve success/running of urgent fire avoidance
        a.Hex("F049FF45404D896560"); // disconnected checks++, last unit
        a.Hex("410FB6442437"); // command byte
        foreach (byte order in new byte[] { 2, 3, 11, 12 }) { a.Hex("83F8" + order.ToString("X2")); a.Jump("84", "returning"); }
        a.Hex("498B4D28488BD64D8BC4"); a.Call(exe + sites.Recover);
        a.Hex("83F801"); a.Jump("84", "recovered");
        a.Hex("41F6452002"); a.Jump("85", "hold");
        a.Hex("498B4D38488BD64D8BC4"); a.Call(exe + sites.Retreat);
        a.Hex("83F801"); a.Jump("85", "hold");
        a.Hex("F049FF455049C7456804000000"); a.Jump("", "success");
        a.Label("hold"); a.Hex("41807C24370D"); a.Jump("84", "held");
        a.Hex("4C89E1"); a.Call(exe + sites.Hold);
        a.Label("held"); a.Hex("F049FF455849C7456805000000"); a.Jump("", "success");
        a.Label("recovered"); a.Hex("F049FF454849C7456803000000"); a.Jump("", "success");
        a.Label("returning"); a.Hex("49C7456802000000");
        a.Label("success"); a.Hex("B801000000"); a.Jump("", "done");
        a.Label("original");
        // Keep the incoming agent outside all callees' shadow space, including on early bypass paths.
        a.Hex("488BCB488BD64C8B442420"); a.Call(exe + sites.Selector);
        a.Label("done"); a.Hex("4883C430415D415C5F5E5BC3");
        return a.Finish();
    }

    // Atomic compare/exchange of the node vptr and the configuration transaction on the game's frame.
    // RCX=node, RDX=expected vptr, R8=desired vptr, R9=expected config with a unique transaction tag.
    // +0x80 holds the requested config. Cancellation removes the tag, so a late frame cannot apply it.
    internal static byte[] Install(ulong data)
    {
        var a = new Assembler();
        a.Hex("488BC2F04C0FB101"); a.Jump("85", "failed");
        a.Immediate("49BA", data);
        a.Hex("498BC1498B9280000000F0490FB15220"); a.Jump("85", "failed");
        a.Hex("B801000000C3");
        a.Label("failed"); a.Hex("33C0C3");
        return a.Finish();
    }

    private sealed class Assembler
    {
        private readonly List<byte> bytes = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> jumps = [];
        internal void Hex(string hex) => bytes.AddRange(Convert.FromHexString(hex));
        internal void Immediate(string prefix, ulong value) { Hex(prefix); bytes.AddRange(BitConverter.GetBytes(value)); }
        internal void Call(ulong address) { Immediate("48B8", address); Hex("FFD0"); }
        internal void Label(string label) => labels.Add(label, bytes.Count);
        internal void Jump(string condition, string label)
        {
            Hex(condition.Length == 0 ? "E9" : "0F" + condition);
            jumps.Add((bytes.Count, label)); bytes.AddRange(new byte[4]);
        }
        internal byte[] Finish()
        {
            byte[] code = bytes.ToArray();
            foreach ((int offset, string label) in jumps) BitConverter.TryWriteBytes(code.AsSpan(offset), labels[label] - offset - 4);
            return code;
        }
    }
}
