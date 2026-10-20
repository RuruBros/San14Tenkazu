namespace San14Tenkazu;

// Pin both rulers when the user chooses the batch. A changed force must never silently become another target.
internal static class ForceSurrenders
{
    internal readonly record struct Request(int From, int FromRuler, int To, int ToRuler);

    internal static List<Request> Prepare(GameSnapshot data, IEnumerable<int> sources, int target)
    {
        int[] ids = sources.Distinct().ToArray();
        if (ids.Length == 0) throw new InvalidOperationException(L.Surrender.NoSources);
        var requests = ids.Select(from => new Request(from, data.Forces.GetValueOrDefault(from).RulerId,
            target, data.Forces.GetValueOrDefault(target).RulerId)).ToList();
        foreach (Request request in requests) Validate(data, request);
        return requests;
    }

    internal static void Validate(GameSnapshot data, Request request)
    {
        if (request.From == data.PlayerForce) throw new InvalidOperationException(L.Surrender.PlayerForce);
        if (request.From == request.To || request.From is < 1 or > GameMemory.MaxPlayerForce ||
            request.To is < 1 or > GameMemory.MaxPlayerForce ||
            BaseGrants.Ruler(data, request.From)?.Id != request.FromRuler ||
            BaseGrants.Ruler(data, request.To)?.Id != request.ToRuler)
            throw new InvalidOperationException(L.Surrender.Changed);
    }
}
