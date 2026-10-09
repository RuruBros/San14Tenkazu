using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace San14Tenkazu.Tests;

public sealed class KtglArchiveTests
{
    [Fact]
    public void ReadsRawResourceWithLinksExtraHeaderAndEntryOffset()
    {
        byte[] expected = Payload(101);
        byte[] entry = Entry(expected, compressed: false);
        WithFile(entry, (stream, offset) => Assert.Equal(expected, KtglArchive.Read(stream, offset, entry.Length)));
    }

    [Fact]
    public void InflatesMultipleChunksWithoutReadingTheNextEntry()
    {
        byte[] expected = Payload(200_123);
        byte[] entry = Entry(expected, compressed: true);
        WithFile(entry, (stream, offset) =>
        {
            Assert.Equal(expected, KtglArchive.Read(stream, offset, entry.Length));
            Assert.Equal(offset + entry.Length, stream.Position);
            Assert.Equal(0x5A, stream.ReadByte());
        });
    }

    [Fact]
    public void AllocatesOneOwnedOutputInsteadOfFullEntryAndOutputCopies()
    {
        byte[] expected = Payload(1_000_123);
        byte[] entry = Entry(expected, compressed: true);
        WithFile(entry, (stream, offset) =>
        {
            _ = KtglArchive.Read(stream, offset, entry.Length);
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] actual = KtglArchive.Read(stream, offset, entry.Length);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected, actual);
            Assert.InRange(allocated, expected.Length, expected.Length * 3L / 2);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamsToConsumerWithoutAllocatingWholeResource(bool compressed)
    {
        byte[] expected = Payload(1_000_123);
        byte[] entry = Entry(expected, compressed);
        WithFile(entry, (stream, offset) =>
        {
            _ = KtglArchive.Read(stream, offset, entry.Length); // warm the small chunk pool
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var output = new HashOutput(hash);
            long before = GC.GetAllocatedBytesForCurrentThread();
            KtglArchive.ReadTo(stream, offset, entry.Length, length => { Assert.Equal(expected.Length, length); return output; });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected.Length, output.Written);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(expected), hash.GetHashAndReset());
            Assert.InRange(allocated, 0, 100_000);
            Assert.Equal(offset + entry.Length, stream.Position);
            Assert.Equal(0x5A, stream.ReadByte());
            Assert.True(output.IsDisposed);
        });
    }

    [Fact]
    public void DisposesConsumerWhenDecompressionFails()
    {
        byte[] entry = Entry(Payload(123), compressed: true);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0x18), 124);
        WithFile(entry, (stream, offset) =>
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var output = new HashOutput(hash);
            Assert.Throws<InvalidDataException>(() => KtglArchive.ReadTo(stream, offset, entry.Length, _ => output));
            Assert.True(output.IsDisposed);
        });
    }

    private sealed class HashOutput(System.Security.Cryptography.IncrementalHash hash) : Stream
    {
        internal int Written;
        internal bool IsDisposed;
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); Written += buffer.Length; }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void RejectsInvalidCompressedChunkBounds(int chunkSize)
    {
        byte[] entry = Entry(Payload(123), compressed: true);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(0x38 + 24), chunkSize);
        WithFile(entry, (stream, offset) => Assert.Throws<InvalidDataException>(() => KtglArchive.Read(stream, offset, entry.Length)));
    }

    [Fact]
    public void RejectsTruncatedInflatedResource()
    {
        byte[] entry = Entry(Payload(200_123), compressed: true);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0x18), 200_124);
        WithFile(entry, (stream, offset) => Assert.Throws<InvalidDataException>(() => KtglArchive.Read(stream, offset, entry.Length)));
    }

    [Fact]
    public void RejectsOutputLargerThanDeclaredResource()
    {
        byte[] entry = Entry(Payload(123), compressed: true);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0x18), 122);
        WithFile(entry, (stream, offset) => Assert.Throws<InvalidDataException>(() => KtglArchive.Read(stream, offset, entry.Length)));
    }

    [Fact]
    public void RejectsEntryExtendingPastArchiveEnd()
    {
        byte[] entry = Entry(Payload(123), compressed: true);
        WithFile(entry, (stream, offset) => Assert.Throws<InvalidDataException>(() => KtglArchive.Read(stream, offset, entry.Length + 2)));
    }

    private static byte[] Payload(int length)
    {
        byte[] data = new byte[length]; new Random(14).NextBytes(data); return data;
    }

    private static byte[] Entry(byte[] data, bool compressed)
    {
        using var payload = new MemoryStream();
        if (compressed)
        {
            for (int offset = 0; offset < data.Length; offset += 65_536)
            {
                using var chunk = new MemoryStream();
                using (var zlib = new ZLibStream(chunk, CompressionLevel.Fastest, leaveOpen: true))
                    zlib.Write(data.AsSpan(offset, Math.Min(65_536, data.Length - offset)));
                using var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write((int)chunk.Length); writer.Write(chunk.ToArray());
            }
        }
        else payload.Write(data);
        var entry = new byte[0x38 + 24 + payload.Length];
        "IDRK0000"u8.CopyTo(entry);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), entry.Length);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0x10), payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0x18), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(0x20), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0x2C), compressed ? 0x00100000u : 0u);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(0x34), 1);
        payload.ToArray().CopyTo(entry, 0x38 + 24);
        return entry;
    }

    private static void WithFile(byte[] entry, Action<FileStream, long> check)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ktgl");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            stream.Write(new byte[37]); stream.Write(entry); stream.WriteByte(0x5A); stream.Flush();
            check(stream, 37);
        }
        finally { File.Delete(path); }
    }
}
