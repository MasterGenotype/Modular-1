namespace Modular.FluentHttp.Interfaces;

/// <summary>
/// Filter that can modify requests before sending and responses after receiving.
/// </summary>
public interface IHttpFilter
{
    /// <summary>
    /// Called before a request is sent.
    /// </summary>
    void OnRequest(IRequest request);

    /// <summary>
    /// Called after a response is received.
    /// </summary>
    void OnResponse(IResponse response, bool httpErrorAsException);

    /// <summary>
    /// Name of the filter for identification.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Priority for filter ordering (lower = earlier).
    /// </summary>
    int Priority { get; }
}

/// <summary>
/// Collection of HTTP filters.
/// </summary>
public class FilterCollection
{
    private readonly List<IHttpFilter> _filters = [];

    public void Add(IHttpFilter filter) => _filters.Add(filter);

    public void Remove(IHttpFilter filter) => _filters.Remove(filter);

    public void RemoveAll<T>() where T : IHttpFilter => _filters.RemoveAll(f => f is T);

    public void Clear() => _filters.Clear();

    public IEnumerable<IHttpFilter> GetOrdered() => _filters.OrderBy(f => f.Priority);

    public int Count => _filters.Count;
}
