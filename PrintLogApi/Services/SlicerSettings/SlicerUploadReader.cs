using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace PrintLogApi.Services.SlicerSettings;

/// <summary>Why an upload was refused: the status, and a message safe to return to the caller.</summary>
public sealed class SlicerUploadRejectedException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}

/// <summary>
/// Reads the OrcaSlicer plugin's upload (<c>POST /api/Slicer/uploads</c>) under hard caps, so no
/// request can make the server hold more than the caps in memory: the body is counted as it is
/// read (Kestrel's <c>[RequestSizeLimit]</c> does the same in production, but the test server has
/// no such feature), the <c>metadata</c> part has its own cap, and the G-code is inflated into a
/// bounded buffer, so a gzip bomb stops at the text cap rather than at the end of the bomb.
/// </summary>
public static class SlicerUploadReader
{
    public sealed record Parts(byte[] Gcode, byte[] Metadata);

    /// <summary>The two parts, read under the caps. Other parts are read past and dropped.</summary>
    /// <exception cref="SlicerUploadRejectedException">415, 413 or 400.</exception>
    public static async Task<Parts> ReadAsync(HttpRequest request, int maxRequestBytes, int maxMetadataBytes, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new SlicerUploadRejectedException(
                StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send multipart/form-data with a gcode part and a metadata part.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 70)
        {
            throw Invalid("The multipart boundary is missing or longer than 70 characters.");
        }

        if (request.ContentLength > maxRequestBytes)
        {
            throw RequestTooLarge(maxRequestBytes);
        }

        var body = new CountingReadStream(request.Body, maxRequestBytes);
        var reader = new MultipartReader(boundary, body) { BodyLengthLimit = maxRequestBytes };
        byte[]? gcode = null;
        byte[]? metadata = null;

        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                var name = ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    // Not IsFormDisposition(): that is false for a part with a filename, as the gcode part has.
                    && disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                    ? HeaderUtilities.RemoveQuotes(disposition.Name).Value
                    : null;

                switch (name)
                {
                    case "gcode":
                        gcode = gcode is null
                            ? await ReadAllAsync(section.Body, maxRequestBytes, ct) ?? throw RequestTooLarge(maxRequestBytes)
                            : throw Duplicate(name);
                        break;
                    case "metadata":
                        metadata = metadata is null
                            ? await ReadAllAsync(section.Body, maxMetadataBytes, ct)
                                ?? throw new SlicerUploadRejectedException(
                                    StatusCodes.Status413PayloadTooLarge, "Upload is too large", $"metadata is over {maxMetadataBytes / 1024} KiB.")
                            : throw Duplicate(name);
                        break;
                    default:
                        await section.Body.CopyToAsync(Stream.Null, ct);
                        break;
                }
            }
        }
        catch (CountingReadStream.LimitExceededException)
        {
            throw RequestTooLarge(maxRequestBytes);
        }
        catch (BadHttpRequestException e)
        {
            // Kestrel's own [RequestSizeLimit] check (413), or a body it could not read (400).
            throw new SlicerUploadRejectedException(e.StatusCode, "Bad request body", e.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? $"The request is over {maxRequestBytes / (1024 * 1024)} MiB."
                : "The request body could not be read.");
        }
        catch (Exception e) when (e is InvalidDataException or IOException && !ct.IsCancellationRequested)
        {
            // MultipartReader's errors for a body that is not well-formed multipart.
            throw Invalid("The body is not well-formed multipart/form-data.");
        }

        return new Parts(
            gcode ?? throw Invalid("The gcode part is missing."),
            metadata ?? throw Invalid("The metadata part is missing."));
    }

    /// <summary>
    /// The UTF-8 text in <paramref name="gzip"/>, inflated into a buffer of at most
    /// <paramref name="maxChars"/> characters: decompression stops as soon as the text passes the
    /// cap, so the work and the memory are bounded by the cap, not by what the gzip expands to.
    /// </summary>
    /// <exception cref="SlicerUploadRejectedException">400 for data that is not gzip; 413 past the cap.</exception>
    public static string Gunzip(byte[] gzip, int maxChars)
    {
        try
        {
            using var input = new MemoryStream(gzip, writable: false);
            using var inflated = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(inflated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);

            var text = new StringBuilder();
            var buffer = new char[64 * 1024];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + read > maxChars)
                {
                    throw new SlicerUploadRejectedException(
                        StatusCodes.Status413PayloadTooLarge,
                        "Upload is too large",
                        $"The G-code is over {maxChars:N0} characters. Send its first and last 2,000,000 bytes joined by a newline.");
                }

                text.Append(buffer, 0, read);
            }

            return text.ToString();
        }
        catch (InvalidDataException)
        {
            throw Invalid("The gcode part is not gzip data.");
        }
    }

    /// <summary>The whole part, or null when it is longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadAllAsync(Stream part, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await part.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static SlicerUploadRejectedException Invalid(string detail) =>
        new(StatusCodes.Status400BadRequest, "Invalid upload", detail);

    private static SlicerUploadRejectedException Duplicate(string part) =>
        Invalid($"The {part} part was sent more than once.");

    private static SlicerUploadRejectedException RequestTooLarge(int maxRequestBytes) =>
        new(StatusCodes.Status413PayloadTooLarge, "Upload is too large", $"The request is over {maxRequestBytes / (1024 * 1024)} MiB.");

    /// <summary>Throws once more than <c>limit</c> bytes have been read through it.</summary>
    private sealed class CountingReadStream(Stream inner, long limit) : Stream
    {
        public sealed class LimitExceededException : Exception;

        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new LimitExceededException() : read;
        }
    }
}
