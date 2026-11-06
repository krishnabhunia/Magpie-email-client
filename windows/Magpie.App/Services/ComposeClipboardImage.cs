using System.Windows.Media.Imaging;

namespace Magpie.App.Services;

/// <summary>Bounded local PNG encoding; no clipboard, filesystem or network access.</summary>
public static class ComposeClipboardImage
{
    public const long MaximumPixels = 40_000_000;
    public const int InlineLimitBytes = 8 * 1024 * 1024;
    public const int AttachmentLimitBytes = 32 * 1024 * 1024;

    public static byte[] EncodePng(BitmapSource image, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelWidth < 1 || image.PixelHeight < 1 || (long)image.PixelWidth * image.PixelHeight > MaximumPixels)
            throw new InvalidDataException("This clipboard picture is too large. Resize it or attach the original file instead.");
        if (maximumBytes < 1 || maximumBytes > AttachmentLimitBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var output = new BoundedPngStream(maximumBytes);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed class BoundedPngStream(long limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > limit) throw new InvalidDataException("The clipboard PNG exceeds the safe size limit. Resize it or attach the original file.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > limit) throw new InvalidDataException("The clipboard PNG exceeds the safe size limit. Resize it or attach the original file.");
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Position >= limit) throw new InvalidDataException("The clipboard PNG exceeds the safe size limit.");
            base.WriteByte(value);
        }
        public override void SetLength(long value)
        {
            if (value > limit) throw new InvalidDataException("The clipboard PNG exceeds the safe size limit.");
            base.SetLength(value);
        }
    }
}
