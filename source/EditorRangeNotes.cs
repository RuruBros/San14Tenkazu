namespace San14Tenkazu;

internal static class EditorRangeNotes
{
    internal static string Evidence(FeatureRangeEvidence evidence)
    {
        var lines = new List<string>();
        if (evidence.Files.Length > 0) lines.Add(L.EditRanges.Files(string.Join(", ", evidence.Files)));
        else if (!evidence.Incomplete) lines.Add(L.EditRanges.NoFiles);
        if (evidence.Incomplete) lines.Add(L.EditRanges.Incomplete);
        lines.Add(evidence.Settings.Length > 0
            ? L.EditRanges.Settings(string.Join(", ", evidence.Settings)) : L.EditRanges.NoSettings);
        return string.Join("\n", lines);
    }
}
