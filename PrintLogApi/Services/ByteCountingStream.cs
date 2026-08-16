namespace PrintLogApi.Services;

/// <summary>
/// A write-only pass-through that records how many bytes reached the wrapped stream.
///
/// Streaming the CSV export straight to the response body means nothing ever holds the whole
/// report, so <c>ReportLengthInBytes</c> can no longer be read off a <see cref="MemoryStream"/>
/// after the fact — it has to be tallied as the bytes go past. Counting here rather than in the
/// writer counts the encoded bytes, which is what the metric has always meant.
///
/// Disposal deliberately does <b>not</b> dispose the inner stream: the caller owns it, and for the
/// export that caller is ASP.NET Core, which still needs the response body afterwards.
/// </summary>
internal sealed class ByteCountingStream(Stream inner) : Stream
{
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        BytesWritten += buffer.Length;
    }

    public override async Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        BytesWritten += count;
    }

    public override void WriteByte(byte value)
    {
        inner.WriteByte(value);
        BytesWritten += 1;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
