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
    public Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
        => UploadCoreAsync(containerName, blobName, stream, null);

    /// <summary>Uploads with metadata, which <see cref="Metadata"/> and the listing then show.</summary>
    public Task<BlobUploadResult> UploadAsync(
        string containerName, string blobName, Stream stream, IReadOnlyDictionary<string, string> metadata)
        => UploadCoreAsync(containerName, blobName, stream, metadata);

    /// <summary>Metadata of the blobs uploaded with some, keyed as <see cref="Blobs"/> is.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Metadata { get; } = new();

    /// <summary>The blobs under <paramref name="containerName"/>, in name order.</summary>
    public async IAsyncEnumerable<StoredBlob> ListBlobsAsync(
        string containerName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefix = containerName + "/";
        var listed = Blobs
            .Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var (path, bytes) in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StoredBlob(
                path[prefix.Length..], null, bytes.LongLength,
                Metadata.TryGetValue(path, out var metadata) ? metadata : new Dictionary<string, string>());
        }
    }

    private async Task<BlobUploadResult> UploadCoreAsync(
        string containerName, string blobName, Stream stream, IReadOnlyDictionary<string, string>? metadata)
    {
        var blobPath = $"{containerName}/{blobName}";
        if (metadata is null)
            Metadata.Remove(blobPath);
        else
            Metadata[blobPath] = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

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

    /// <summary>How many times <see cref="OpenReadAsync"/> was called.</summary>
    public int OpenReadCount { get; private set; }

    /// <summary>
    /// Opens a blob from in-memory storage, forward-only unless <paramref name="seekable"/>, as
    /// the Azure implementation's single streaming download is.
    /// </summary>
    public Task<BlobContent?> OpenReadAsync(
        string containerName, string blobName, bool seekable, CancellationToken cancellationToken = default)
    {
        OpenReadCount++;
        if (!Blobs.TryGetValue($"{containerName}/{blobName}", out var bytes))
            return Task.FromResult<BlobContent?>(null);

        Stream stream = new MemoryStream(bytes, writable: false);
        if (!seekable)
            stream = new ForwardOnlyStream(stream);
        return Task.FromResult<BlobContent?>(new BlobContent(stream, bytes.LongLength));
    }

    /// <summary>A read-only stream that cannot seek or report its length.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Lengths to report in place of a blob's real byte count, keyed as <see cref="Blobs"/> is,
    /// so a test can stand in a 300 MB blob without allocating one.
    /// </summary>
    public Dictionary<string, long> ReportedLengths { get; } = new();

    /// <summary>
    /// The blob's length: <see cref="ReportedLengths"/> when set, else its byte count. Null when
    /// it does not exist.
    /// </summary>
    public Task<long?> GetBlobLengthAsync(
        string containerName, string blobName, CancellationToken cancellationToken = default)
    {
        var blobPath = $"{containerName}/{blobName}";
        if (!Blobs.TryGetValue(blobPath, out var bytes))
            return Task.FromResult<long?>(null);
        return Task.FromResult<long?>(
            ReportedLengths.TryGetValue(blobPath, out var reported) ? reported : bytes.LongLength);
    }

    /// <summary>
    /// Removes the blob from in-memory storage. No-op if it does not exist.
    /// </summary>
    public Task DeleteBlobAsync(string containerName, string blobName)
    {
        DeletedBlobNames.Add($"{containerName}/{blobName}");
        Blobs.Remove($"{containerName}/{blobName}");
        Metadata.Remove($"{containerName}/{blobName}");
        ReportedLengths.Remove($"{containerName}/{blobName}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all stored blobs (useful between tests).
    /// </summary>
    public void Clear()
    {
        Blobs.Clear();
        Metadata.Clear();
        ReportedLengths.Clear();
        UploadedBlobNames.Clear();
        DeletedBlobNames.Clear();
        SignCallCount = 0;
        OpenReadCount = 0;
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
