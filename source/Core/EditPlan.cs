namespace San14Tenkazu;

// What a rule plans to write for something the user asked for: the edits (with whatever must change along with
// them), and in a line or two what they do, for the question asked before writing. A refused plan writes nothing
// and says why.
internal sealed record EditPlan(IReadOnlyList<RecordEdit> Edits, string Summary, string Refusal = "")
{
    internal bool Refused => Refusal.Length > 0;

    internal static EditPlan Refuse(string reason) => new([], "", reason);
}
