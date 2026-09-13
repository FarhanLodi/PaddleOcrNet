namespace PaddleOcrNet.Tests;

/// <summary>
/// A write-only, non-seekable wrapper used to prove the searchable PDF writer tracks offsets itself.
/// </summary>
internal sealed class PdfNonSeekableStream : Stream
{
    private readonly Stream _inner;

    public PdfNonSeekableStream(Stream inner) => _inner = inner;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
}
