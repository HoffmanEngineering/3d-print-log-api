using PrintLogApi.Caching;
using PrintLogApi.Models;
using PrintLogApi.Models.SortEnums;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

public class PrintSummaryCachePolicyTests
{
    private sealed record Shape(
        int PageNumber = 1,
        int PageSize = PrintSummaryCachePolicy.ProfilePageSize,
        string? SearchText = null,
        long[]? PrinterIds = null,
        Guid[]? FilamentIds = null,
        PrintSummarySortColumn SortColumn = PrintSummarySortColumn.StartDate,
        SortDirection SortDirection = SortDirection.Desc,
        PrintStatus[]? Statuses = null,
        Guid[]? ProjectIds = null,
        DateTimeOffset? FromDate = null,
        DateTimeOffset? ToDate = null);

    private static bool ShouldCache(bool viewerIsOwner, Shape s) =>
        PrintSummaryCachePolicy.ShouldCache(
            viewerIsOwner,
            new PagedRequest { PageNumber = s.PageNumber, PageSize = s.PageSize },
            s.SearchText,
            s.PrinterIds ?? [],
            s.FilamentIds ?? [],
            new SortRequest<PrintSummarySortColumn> { SortColumn = s.SortColumn, SortDirection = s.SortDirection },
            s.Statuses ?? [],
            s.ProjectIds ?? [],
            s.FromDate,
            s.ToDate);

    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, bool> VisitorShapes() => new()
    {
        { "profile page 1", true },
        { "profile page 5", true },
        { "profile with a status", true },
        { "project page", true },
        { "page 6", false },
        { "page 0", false },
        { "search text", false },
        { "date range", false },
        { "printer id", false },
        { "filament id", false },
        { "title sort", false },
        { "ascending", false },
        { "no sort", false },
        { "two statuses", false },
        { "page size 250", false },
        { "page size 100 without a project", false },
        { "page size 10 with a project", false },
        { "two projects", false },
    };

    private static Shape ShapeFor(string name) => name switch
    {
        "profile page 1" => new Shape(),
        "profile page 5" => new Shape(PageNumber: 5),
        "profile with a status" => new Shape(Statuses: [PrintStatus.Success]),
        "project page" => new Shape(PageSize: PrintSummaryCachePolicy.ProjectPageSize, ProjectIds: [Guid.NewGuid()]),
        "page 6" => new Shape(PageNumber: 6),
        "page 0" => new Shape(PageNumber: 0),
        "search text" => new Shape(SearchText: "benchy"),
        "date range" => new Shape(FromDate: From, ToDate: From.AddDays(1)),
        "printer id" => new Shape(PrinterIds: [1]),
        "filament id" => new Shape(FilamentIds: [Guid.NewGuid()]),
        "title sort" => new Shape(SortColumn: PrintSummarySortColumn.Title),
        "ascending" => new Shape(SortDirection: SortDirection.Asc),
        "no sort" => new Shape(SortColumn: default, SortDirection: default),
        "two statuses" => new Shape(Statuses: [PrintStatus.Success, PrintStatus.Failed]),
        "page size 250" => new Shape(PageSize: 250),
        "page size 100 without a project" => new Shape(PageSize: PrintSummaryCachePolicy.ProjectPageSize),
        "page size 10 with a project" => new Shape(ProjectIds: [Guid.NewGuid()]),
        "two projects" => new Shape(PageSize: PrintSummaryCachePolicy.ProjectPageSize, ProjectIds: [Guid.NewGuid(), Guid.NewGuid()]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Theory]
    [MemberData(nameof(VisitorShapes))]
    public void AReaderWhoIsNotTheOwner_IsCachedOnlyForThePublicPagesShapes(string shape, bool expected)
    {
        Assert.Equal(expected, ShouldCache(viewerIsOwner: false, ShapeFor(shape)));
    }

    [Theory]
    [MemberData(nameof(VisitorShapes))]
    public void TheOwner_IsCachedWhateverTheShape(string shape, bool _)
    {
        Assert.True(ShouldCache(viewerIsOwner: true, ShapeFor(shape)));
    }

    [Fact]
    public void ANullSortRequest_IsNotCachedForAVisitor()
    {
        Assert.False(PrintSummaryCachePolicy.ShouldCache(
            false, new PagedRequest(), null, [], [], null, [], [], null, null));
    }
}
