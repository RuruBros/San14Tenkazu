namespace San14Tenkazu;

// Resources acquired during construction have no owner until the constructor returns. Release
// them in reverse order on every failure; Commit transfers them to the completed object.
internal sealed class ResourceScope(Action<IDisposable>? acquired = null) : IDisposable
{
    private readonly Stack<IDisposable> resources = new();

    internal T Own<T>(T resource) where T : IDisposable
    {
        resources.Push(resource);
        acquired?.Invoke(resource);
        return resource;
    }

    internal void Commit() => resources.Clear();

    public void Dispose()
    {
        List<Exception>? failures = null;
        while (resources.TryPop(out var resource))
            try { resource.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException(failures);
    }
}
