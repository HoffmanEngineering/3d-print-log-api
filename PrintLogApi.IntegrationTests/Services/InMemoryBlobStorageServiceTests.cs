using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services
{
    public class InMemoryBlobStorageServiceTests
    {
        [Fact]
        public async Task DeleteBlobAsync_ExistingBlob_RemovesFromStorage()
        {
            var service = new InMemoryBlobStorageService();
            await service.UploadAsync("container", "test.txt", new MemoryStream(new byte[] { 1, 2, 3 }));
            Assert.True(service.BlobExists("container/test.txt"));

            await service.DeleteBlobAsync("container", "test.txt");

            Assert.False(service.BlobExists("container/test.txt"));
        }

        [Fact]
        public async Task DeleteBlobAsync_NonExistentBlob_DoesNotThrow()
        {
            var service = new InMemoryBlobStorageService();
            // Should not throw
            await service.DeleteBlobAsync("container", "does-not-exist.txt");
        }
    }
}
