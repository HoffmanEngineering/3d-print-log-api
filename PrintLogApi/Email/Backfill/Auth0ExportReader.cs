using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintLogApi.Email.Backfill;

/// <summary>One line of an Auth0 users-export job, reduced to the fields the backfill needs.</summary>
public record Auth0ExportUser(string UserId, string? Email, bool EmailVerified, DateTimeOffset? CreatedAt);

/// <summary>
/// Reads the NDJSON file an Auth0 users-export job produces (scripts/auth0-export-users.ps1).
/// Auth0 serves it gzipped; a file someone already unzipped reads the same, detected by the gzip
/// magic bytes rather than the file name.
/// </summary>
public static class Auth0ExportReader
{
    private sealed record Line(
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool? EmailVerified,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);

    public static async IAsyncEnumerable<Auth0ExportUser> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var magic = new byte[2];
        var read = await stream.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, ct);
        var isGzip = read == 2 && magic[0] == 0x1F && magic[1] == 0x8B;

        // The two peeked bytes are stitched back in front of the rest, so a non-seekable source
        // (a pipe, an HTTP body) works as well as a file.
        Stream source = new PrefixedStream(magic.AsMemory(0, read), stream);
        if (isGzip)
        {
            source = new GZipStream(source, CompressionMode.Decompress);
        }

        using var reader = new StreamReader(source);
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var line = JsonSerializer.Deserialize<Line>(text);
            if (string.IsNullOrWhiteSpace(line?.UserId))
            {
                continue;
            }

            yield return new Auth0ExportUser(line.UserId, line.Email, line.EmailVerified == true, line.CreatedAt);
        }
    }

    /// <summary>A read-only stream that yields a prefix and then the rest of an inner stream.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.IsEmpty)
            {
                return rest.Read(buffer);
            }

            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.IsEmpty)
            {
                return await rest.ReadAsync(buffer, cancellationToken);
            }

            return Read(buffer.Span);
        }
    }
}
