using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>The complete serialized MIME size, or a safe lower bound when the measurement cap is reached.</summary>
public sealed record EncodedMessageSize(long Bytes, bool AtLeast = false);

public static partial class ComposeSafety
{
    /// <summary>
    /// Counts the actual message headers, body, inline images and all attachment parts without buffering the output.
    /// The caller retains ownership of the MIME message and its attachment streams. No network access occurs.
    /// </summary>
    public static EncodedMessageSize MeasureEncodedMessage(MimeMessage message, long maximumBytes = 200_000_000)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (maximumBytes < 4096 || maximumBytes >= long.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var counter = new MessageSizeCounter(maximumBytes);
        try
        {
            message.WriteTo(FormatOptions.Default, counter);
            return new EncodedMessageSize(counter.Length);
        }
        catch (MessageSizeLimitException)
        {
            return new EncodedMessageSize(maximumBytes + 1, AtLeast: true);
        }
    }

    private sealed class MessageSizeLimitException : Exception { }

    private sealed class MessageSizeCounter(long maximum) : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - _length) throw new MessageSizeLimitException();
            _length += buffer.Length;
        }
        public override void WriteByte(byte value)
        {
            if (_length >= maximum) throw new MessageSizeLimitException();
            _length++;
        }
    }
}
