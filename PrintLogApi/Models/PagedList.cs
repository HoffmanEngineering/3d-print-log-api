using System.ComponentModel;
using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Models;

/// <summary>
/// <para><c>[ImmutableObject(true)]</c> is here for HybridCache and nothing else. It is inert at
/// runtime; the attribute is how HybridCache is told it may hand the same instance to every
/// caller instead of storing a serialized copy in L1 and deserializing it on each hit.</para>
///
/// <para>Without it a cache <i>hit</i> would pay a full JSON deserialize. That is the cost #66
/// declined to spend on the serialize side, and it would land on the endpoints whose expensive
/// part — the SQL aggregation — the cache already skips.</para>
///
/// <para><b>Be clear about what it does and does not claim.</b> This type is mutable — both
/// properties have setters and <c>Items</c> is a mutable <c>List&lt;T&gt;</c> — so the attribute
/// is a statement of convention, not a property the compiler enforces: nothing mutates a
/// <c>PagedList</c> it read out of the cache, and nothing may start.</para>
///
/// <para>What it is not is a new risk. A cached <c>PagedList</c> was already shared by reference
/// across concurrent requests under plain <c>IMemoryCache</c>; the attribute keeps that exact
/// behaviour rather than introducing it. The only alternative is letting HybridCache serialize
/// and re-deserialize per hit, which is a real cost paid on every request to buy protection
/// against a mutation that does not happen. Treat anything returned from a cache lookup as
/// read-only, and if a caller ever genuinely needs to mutate one, copy it first.</para>
/// </summary>
[ImmutableObject(true)]
public class PagedList<T>
{
    public PageInfo Paging { get; set; }

    public List<T> Items { get; set; }

    /// <summary>
    /// For Integration Testing Deserialization Only
    /// </summary>
    public PagedList()
    {
        Paging = new PageInfo(1, 1, 1);
        Items = new List<T>();
    }

    public PagedList(List<T> items, int totalCount, int pageNumber, int pageSize)
    {
        Paging = new PageInfo(totalCount, pageNumber, pageSize);
        Items = items;
    }

    public static PagedList<T> Create(IQueryable<T> source, int pageNumber, int pageSize)
    {
        var count = source.Count();
        var items = source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return new PagedList<T>(items, count, pageNumber, pageSize);
    }

    public static async Task<PagedList<T>> CreateAsync(IQueryable<T> source, int pageNumber, int pageSize)
    {
        var count = await source.CountAsync();
        var items = await source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedList<T>(items, count, pageNumber, pageSize);
    }
}
