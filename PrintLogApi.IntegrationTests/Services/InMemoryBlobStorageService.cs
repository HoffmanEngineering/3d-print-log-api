using PrintLogApi.Services;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// In-memory blob storage implementation for integration testing.
/// Stores files in memory without actual blob storage calls.
/// </summary>
public class InMemoryBlobStorageService : IBlobStorageService
{
    /// <summary>
    /// Dictionary to store uploaded blobs in memory for testing.
    /// Key: container/blobname, Value: file content as bytes
    /// </summary>
    public Dictionary<string, byte[]> Blobs { get; private set; } = new();

    /// <summary>Every blob path uploaded, in order, including ones later deleted.</summary>
    public List<string> UploadedBlobNames { get; } = new();

    /// <summary>Every blob path passed to <see cref="DeleteBlobAsync"/>, in order.</summary>
    public List<string> DeletedBlobNames { get; } = new();

    /// <summary>
    /// The content type of the most recent inline signing request. Signing an original as
    /// "image/webp" would set a response content type the bytes contradict, and only a
    /// recording double can catch that - the URL itself does not carry it.
    /// </summary>
    public string? LastSignedContentType { get; private set; }

    /// <summary>How many inline SAS URLs have been generated.</summary>
    public int SignCallCount { get; private set; }

    /// <summary>
    /// Substituted into the next generated signature. Real bucketed signing deliberately
    /// returns a byte-identical URL within a window, so URL equality cannot distinguish
    /// "signed again" from "read from a cache"; varying this can.
    /// </summary>
    public string NextSignature { get; set; } = "fake-inline";

    /// <summary>
    /// Base URI for test blobs (can be customized for testing).
    /// </summary>
    public Uri BaseUri { get; set; } = new Uri("https://test.blob.core.windows.net/");

    /// <summary>
    /// Uploads a file stream to in-memory storage.
    /// </summary>
    public async Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
    {
        var blobPath = $"{containerName}/{blobName}";

        // Read stream to byte array
        using (var memoryStream = new MemoryStream())
        {
            await stream.CopyToAsync(memoryStream);
            Blobs[blobPath] = memoryStream.ToArray();
        }

        UploadedBlobNames.Add(blobPath);

        // Construct the blob URI from the base URI and blob path
        var blobUri = new Uri(BaseUri, blobPath);

        return new BlobUploadResult
        {
            BlobPath = blobPath,
            BlobUri = blobUri
        };
    }

    /// <summary>
    /// Returns a dummy SAS upload URI for testing purposes.
    /// </summary>
    public Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry)
        => Task.FromResult(new Uri("https://fake-blob-storage.example.com/upload-sas"));

    /// <summary>
    /// Returns a dummy SAS download URI for testing purposes.
    /// </summary>
    public Task<Uri> GenerateSasDownloadUrlAsync(string containerName, string blobName, string contentType, string originalFileName, TimeSpan expiry)
        => Task.FromResult(new Uri("https://fake-blob-storage.example.com/download-sas"));

    /// <summary>
    /// Returns a deterministic dummy inline SAS URI, so URL-stability assertions in tests
    /// that go through this double do not depend on a real signer.
    /// </summary>
    public Task<Uri> GenerateSasInlineUrlAsync(
        string containerName, string blobName, string contentType,
        TimeSpan bucketSize, TimeSpan cacheControlMaxAge)
    {
        LastSignedContentType = contentType;
        SignCallCount++;
        return Task.FromResult(new Uri(BaseUri, $"{containerName}/{blobName}?sig={NextSignature}"));
    }

    /// <summary>
    /// Downloads a blob from in-memory storage. Returns null if it does not exist.
    /// </summary>
    public Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName)
    {
        var blobPath = $"{containerName}/{blobName}";
        if (!Blobs.TryGetValue(blobPath, out var bytes))
            return Task.FromResult<(Stream, string)?>(null);

        Stream ms = new MemoryStream(bytes);
        return Task.FromResult<(Stream, string)?>((ms, blobName));
    }

    /// <summary>
    /// Removes the blob from in-memory storage. No-op if it does not exist.
    /// </summary>
    public Task DeleteBlobAsync(string containerName, string blobName)
    {
        DeletedBlobNames.Add($"{containerName}/{blobName}");
        Blobs.Remove($"{containerName}/{blobName}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all stored blobs (useful between tests).
    /// </summary>
    public void Clear()
    {
        Blobs.Clear();
        UploadedBlobNames.Clear();
        DeletedBlobNames.Clear();
        SignCallCount = 0;
        LastSignedContentType = null;
        NextSignature = "fake-inline";
    }

    /// <summary>
    /// Checks if a blob exists in storage.
    /// </summary>
    public bool BlobExists(string blobPath)
    {
        return Blobs.ContainsKey(blobPath);
    }
}
