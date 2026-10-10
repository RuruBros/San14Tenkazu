namespace San14Tenkazu;

internal sealed record TransferGuard(ulong Address, byte[] Expected, bool Nonzero = false);
internal abstract record TransferStep;
internal sealed record TransferStore(ulong Address, byte[] Value) : TransferStep;
internal sealed record TransferCall(GameMemory.FrameInvocation Invocation, bool MustReturnZero = false, ulong CorpsAt = 0) : TransferStep;

// A one-shot callee of FrameStub. All stale/pending guards run before the first gameplay write.
// The original frame resumes only after membership, corps succession and unit orders agree.
internal static class TransferFrame
{
    internal static byte[] Build(IReadOnlyList<TransferGuard> guards, IReadOnlyList<TransferStep> checks, IReadOnlyList<TransferStep> changes)
    {
        var b = new List<byte>();
        var fail = new List<int>();
        void Hex(string value) => b.AddRange(Convert.FromHexString(value));
        void Imm(string op, ulong value) { Hex(op); b.AddRange(BitConverter.GetBytes(value)); }
        void Failed(string op) { Hex(op); fail.Add(b.Count); b.AddRange(new byte[4]); }
        // 16-byte aligned at every call; preserve nonvolatile RBX and 32-byte shadow space.
        Hex("534883EC30");
        foreach (TransferGuard guard in guards)
        {
            if (guard.Nonzero)
            {
                Imm("48B8", guard.Address); Hex("48833800"); Failed("0F84");
                continue;
            }
            for (int i = 0; i < guard.Expected.Length;)
            {
                int size = guard.Expected.Length - i >= 8 ? 8 : guard.Expected.Length - i >= 4 ? 4 : guard.Expected.Length - i >= 2 ? 2 : 1;
                Imm("48B8", guard.Address + (ulong)i);
                byte[] value = new byte[8]; guard.Expected.AsSpan(i, size).CopyTo(value);
                Imm("48BA", BitConverter.ToUInt64(value));
                Hex(size switch { 8 => "483910", 4 => "3910", 2 => "663910", _ => "3810" });
                Failed("0F85"); i += size;
            }
        }
        void Step(TransferStep step)
        {
            if (step is TransferStore store)
            {
                for (int i = 0; i < store.Value.Length; i++) { Imm("48B8", store.Address + (ulong)i); Hex("C600" + store.Value[i].ToString("X2")); }
                return;
            }
            var call = (TransferCall)step;
            ulong[] more = call.Invocation.More ?? [];
            if (more.Length > 4) throw new ArgumentOutOfRangeException(nameof(changes));
            for (int k = 2; k < more.Length; k++)
            {
                Imm("48B8", more[k]);
                Hex("48894424" + (0x20 + 8 * (k - 2)).ToString("X2"));
            }
            Imm("48B9", call.Invocation.Rcx); Imm("48BA", call.Invocation.Rdx);
            if (call.CorpsAt != 0) { Imm("48B8", call.CorpsAt); Hex("0FB710"); }
            Imm("49B8", more.Length > 0 ? more[0] : 0); Imm("49B9", more.Length > 1 ? more[1] : 0);
            Imm("48B8", call.Invocation.Function); Hex("FFD0");
            if (call.MustReturnZero) { Hex("85C0"); Failed("0F85"); }
        }
        foreach (TransferStep check in checks)
        {
            if (check is not TransferCall { MustReturnZero: true }) throw new ArgumentException("Only read-only native guards may precede writes.");
            Step(check);
        }
        foreach (TransferStep change in changes)
        {
            if (change is TransferCall { MustReturnZero: true }) throw new ArgumentException("No rejection is allowed after the first write.");
            Step(change);
        }
        Hex("B801000000");
        int end = b.Count; Hex("EB0233C0"); // success jumps over xor eax,eax
        int failure = end + 2;
        Hex("4883C4305BC3");
        byte[] result = b.ToArray();
        foreach (int at in fail) BitConverter.TryWriteBytes(result.AsSpan(at), failure - at - 4);
        return result;
    }
}
