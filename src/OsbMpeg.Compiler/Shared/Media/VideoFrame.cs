using System.Buffers;

namespace OsbMpeg.Compiler.Shared.Media;

/// <summary>
///     One decoded frame as packed Rgb24 (3 bytes/pixel, row-major, no row padding —
///     ffmpeg's rawvideo/rgb24 output layout). Backed by an ArrayPool buffer; Dispose returns
///     it so streaming decode/analysis never holds the whole video in memory at once.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _buffer;

    private VideoFrame(int width, int height, int index, double pts, byte[] buffer)
    {
        Width = width;
        Height = height;
        Index = index;
        Pts = pts;
        _buffer = buffer;
    }

    public int Width { get; }
    public int Height { get; }
    public int Index { get; }
    public double Pts { get; }
    public int ByteLength => Width * Height * 3;

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public ReadOnlySpan<byte> Rgb => Buffer.AsSpan(0, ByteLength);

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    public static VideoFrame Rent(int width, int height, int index, double pts)
    {
        return new VideoFrame(width, height, index, pts, ArrayPool<byte>.Shared.Rent(width * height * 3));
    }
}