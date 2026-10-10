namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// The product cache. Instead of deleting entries when data changes, every key includes one or more "generations"
/// (change counters), and a write increments them: entries saved under the old numbers are simply never asked for
/// again, and expire on their own. See docs/adr/0008-redis-cache.md.
/// </summary>
/// <remarks>
/// <para>
/// Writes differ in what they change, so there are three kinds of generation:
/// <list type="bullet">
/// <item><description><b>data</b>: moves on with every write (lists filtered by stock);</description></item>
/// <item><description><b>listing</b>: moves on only when products are created, edited or deleted (which products a
/// list or search contains, in what order, and how many);</description></item>
/// <item><description><b>product</b> (one per product): moves on when that product changes, stock included (its
/// details and history).</description></item>
/// </list>
/// A stock change therefore invalidates one product's entries and the stock-level lists, and nothing else.
/// </para>
/// <para>
/// Implementations must <b>fail open</b>: if the cache is unreachable, reads fall back to the database and nothing
/// throws. The cache is a speed-up, never needed for correct answers.
/// </para>
/// </remarks>
internal interface ICatalogCache
{
    /// <summary>
    /// The version an entry in this scope is stored under (made of the generations it depends on, read in one round
    /// trip), or <c>null</c> when the cache is unavailable (callers then bypass it).
    /// </summary>
    Task<string?> GetVersionAsync(CacheScope scope, int productId = 0);

    Task<T?> GetAsync<T>(string key)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class;

    /// <summary>Makes the entries affected by a committed write obsolete. Call after the commit.</summary>
    Task InvalidateAsync(CatalogChange change, IReadOnlyCollection<int> productIds);
}

/// <summary>Which generations a cache entry depends on (and therefore which writes make it obsolete).</summary>
internal enum CacheScope
{
    /// <summary>Depends on everything, stock included: the data generation.</summary>
    Data,

    /// <summary>Depends on which products exist and their names: the listing generation.</summary>
    Listing,

    /// <summary>Depends on one product: the listing generation and that product's generation.</summary>
    Product,
}

/// <summary>What kind of write happened, so the cache knows which generations to increment.</summary>
internal enum CatalogChange
{
    /// <summary>Only stock quantities changed.</summary>
    Stock,

    /// <summary>Products were created, edited or deleted.</summary>
    Products,
}
