using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class FilamentImageModelTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public FilamentImageModelTests(CustomWebApplicationFactory factory) => _factory = factory;

    private IModel Model()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PrintLogContext>().Model;
    }

    [Fact]
    public void FilamentImage_IsMapped()
    {
        Assert.NotNull(Model().FindEntityType(typeof(FilamentImage)));
    }

    [Fact]
    public void DefaultImage_HasAUniqueFilteredIndex()
    {
        // Enforces "exactly one default per filament" in the database. Without it,
        // two concurrent first uploads both become default.
        var entity = Model().FindEntityType(typeof(FilamentImage))!;
        var index = entity.GetIndexes().Single(i =>
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(FilamentImage.FilamentId) &&
            i.IsUnique);

        Assert.NotNull(index.GetFilter());
    }

    [Fact]
    public void DeletingAFilament_CascadesToItsImages()
    {
        var entity = Model().FindEntityType(typeof(FilamentImage))!;
        var fk = entity.GetForeignKeys().Single(f =>
            f.PrincipalEntityType.ClrType == typeof(Filament));

        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void FileReferences_AreRestrictedNotCascaded()
    {
        // File rows are removed explicitly by the service so blobs are deleted too;
        // a cascade would silently orphan blobs.
        var entity = Model().FindEntityType(typeof(FilamentImage))!;
        foreach (var fk in entity.GetForeignKeys()
                     .Where(f => f.PrincipalEntityType.ClrType == typeof(Models.File)))
        {
            Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        }
    }
}
