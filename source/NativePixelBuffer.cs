using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// Temporary pixels for native image APIs. Their lifetime ends with the copy/upload, rather than
// leaving a full-map array in the large object heap until a later collection.
internal sealed class NativePixelBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    internal int Length { get; }
    internal IntPtr Pointer => DangerousGetHandle();
    internal unsafe Span<byte> Bytes
    {
        get { ObjectDisposedException.ThrowIf(IsClosed, this); return new Span<byte>(Pointer.ToPointer(), Length); }
    }

    internal NativePixelBuffer(int length) : base(ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
        SetHandle(Marshal.AllocHGlobal(length));
    }

    internal void ReadExactly(Stream stream)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(Math.Min(Length, 64 * 1024));
        try
        {
            for (int offset = 0; offset < Length;)
            {
                int count = Math.Min(chunk.Length, Length - offset);
                stream.ReadExactly(chunk.AsSpan(0, count));
                Marshal.Copy(chunk, 0, Pointer + offset, count);
                offset += count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    internal static NativePixelBuffer Copy(WMI.BitmapSource source, int stride)
    {
        var buffer = new NativePixelBuffer(checked(stride * source.PixelHeight));
        try { source.CopyPixels(System.Windows.Int32Rect.Empty, buffer.Pointer, buffer.Length, stride); return buffer; }
        catch { buffer.Dispose(); throw; }
    }

    // BGRA and premultiplied BGRA have identical bytes only when every alpha is 255.
    internal bool IsOpaque32()
    {
        if (Length % 4 != 0) return false;
        try
        {
            var bytes = Bytes;
            for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 255) return false;
            return true;
        }
        finally { GC.KeepAlive(this); }
    }

    internal static NativePixelBuffer ReadArchive(string directory, KtglArchive.Entry entry)
    {
        NativePixelBuffer? buffer = null;
        try
        {
            KtglArchive.ReadTo(directory, entry, length => { buffer = new(length); return new Writer(buffer); });
            return buffer!;
        }
        catch { buffer?.Dispose(); throw; }
    }

    private sealed class Writer(NativePixelBuffer buffer) : Stream
    {
        private int position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(buffer.Bytes.Slice(position, bytes.Length));
            position += bytes.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }
}
