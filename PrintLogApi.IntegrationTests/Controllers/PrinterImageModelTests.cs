using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrinterImageModelTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private IModel Model()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PrintLogContext>().Model;
    }

    [Fact]
    public void PrinterImage_IsMapped()
    {
        Assert.NotNull(Model().FindEntityType(typeof(PrinterImage)));
    }

    [Fact]
    public void DefaultImage_HasAUniqueIndexFilteredOnIsDefault()
    {
        // Enforces "at MOST one default per printer" - a filtered unique index cannot
        // require that a default exists. Without it, two concurrent first uploads both
        // become default.
        var entity = Model().FindEntityType(typeof(PrinterImage))!;
        var index = entity.GetIndexes().Single(i =>
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(PrinterImage.PrinterId) &&
            i.IsUnique);

        // The predicate itself, not merely that some filter exists: a regression to
        // [IsDefault] = 0 would invert the constraint and still pass a null check.
        Assert.Equal("[IsDefault] = 1", index.GetFilter());
    }

    [Fact]
    public void DisplayOrder_IsIndexedAlongsideThePrinter()
    {
        var entity = Model().FindEntityType(typeof(PrinterImage))!;

        Assert.Contains(entity.GetIndexes(), i =>
            i.Properties.Count == 2 &&
            i.Properties[0].Name == nameof(PrinterImage.PrinterId) &&
            i.Properties[1].Name == nameof(PrinterImage.DisplayOrder));
    }

    [Fact]
    public void DeletingAPrinter_CascadesToItsImages()
    {
        var entity = Model().FindEntityType(typeof(PrinterImage))!;
        var fk = entity.GetForeignKeys().Single(f =>
            f.PrincipalEntityType.ClrType == typeof(Printer));

        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void FileReferences_AreRestrictedNotCascaded()
    {
        // File rows are removed explicitly by the service so blobs are deleted too;
        // a cascade would silently orphan blobs.
        var entity = Model().FindEntityType(typeof(PrinterImage))!;
        foreach (var fk in entity.GetForeignKeys()
                     .Where(f => f.PrincipalEntityType.ClrType == typeof(Models.File)))
        {
            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        }
    }

    [Fact]
    public void Printers_AreIndexedByOwnerForTenantQueries()
    {
        // The existing covering index is led by Printer.Id, which does not serve
        // "all printers for this user" - the shape every image query uses.
        var entity = Model().FindEntityType(typeof(Printer))!;

        Assert.Contains(entity.GetIndexes(), i =>
            i.Properties.Count == 2 &&
            i.Properties[0].Name == nameof(Printer.UserId) &&
            i.Properties[1].Name == nameof(Printer.IsActive));
    }
}
