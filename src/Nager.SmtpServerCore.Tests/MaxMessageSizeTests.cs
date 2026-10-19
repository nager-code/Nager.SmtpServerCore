using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Protocol;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    /// <summary>
    /// Verifies that the message content a client can make the server buffer is limited by default.
    /// </summary>
    public sealed class MaxMessageSizeTests
    {
        const long SixtyFourMegabytes = 64L * 1024 * 1024;

        static CancellationToken Cancellation => TestContext.Current.CancellationToken;

        [Fact]
        public void Default_IsAStrictLimit()
        {
            var options = new SmtpServerOptionsBuilder().Build();

            Assert.Equal(SmtpServerOptionsBuilder.DefaultMaxMessageSize, options.MaxMessageSizeOptions.Length);
            Assert.Equal(MaxMessageSizeHandling.Strict, options.MaxMessageSizeOptions.Handling);
        }

        [Fact]
        public void MaxMessageSize_IsStrictUnlessSpecifiedOtherwise()
        {
            var options = new SmtpServerOptionsBuilder().MaxMessageSize(1024).Build();

            Assert.Equal(1024, options.MaxMessageSizeOptions.Length);
            Assert.Equal(MaxMessageSizeHandling.Strict, options.MaxMessageSizeOptions.Handling);
        }

        [Fact]
        public void MaxMessageSize_MustNotBeNegative()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SmtpServerOptionsBuilder().MaxMessageSize(-1));
        }

        [Fact]
        public async Task UnterminatedMessage_WithDefaultOptions_IsRejectedBeforeMuchMoreThanTheLimitIsConsumed()
        {
            var options = new SmtpServerOptionsBuilder().Build();
            var stream = new EndlessContentStream(SixtyFourMegabytes);

            var exception = await Assert.ThrowsAsync<SmtpResponseException>(
                () => ReadDotBlockAsync(PipeReader.Create(stream), options.MaxMessageSizeOptions));

            Assert.Same(SmtpResponse.MaxMessageSizeExceeded, exception.Response);
            Assert.True(exception.IsQuitRequested, "the session must be closed");
            Assert.True(stream.BytesProduced < SmtpServerOptionsBuilder.DefaultMaxMessageSize + 1024 * 1024,
                $"the server consumed {stream.BytesProduced} bytes for a message that is limited to {SmtpServerOptionsBuilder.DefaultMaxMessageSize} bytes");
        }

        [Fact]
        public async Task StrictHandling_WithoutLength_DoesNotLimitTheMessage()
        {
            var content = "Subject: test\r\n\r\n" + new string('A', 10000);
            var reader = PipeReader.Create(new MemoryStream(Encoding.ASCII.GetBytes(content + "\r\n.\r\n")));

            var text = await ReadDotBlockAsync(reader, new MaxMessageSizeOptions(MaxMessageSizeHandling.Strict, 0));

            Assert.Equal(content, text);
        }

        [Fact]
        public async Task StrictHandling_WithoutLength_DoesNotLimitCommandLines()
        {
            var reader = PipeReader.Create(new MemoryStream(Encoding.ASCII.GetBytes("NOOP\r\n")));

            var line = await reader.ReadLineAsync(
                Encoding.ASCII,
                new MaxMessageSizeOptions(MaxMessageSizeHandling.Strict, 0),
                SmtpServerOptionsBuilder.DefaultMaxLineLength,
                Cancellation);

            Assert.Equal("NOOP", line);
        }

        static async Task<string?> ReadDotBlockAsync(PipeReader reader, IMaxMessageSizeOptions maxMessageSizeOptions)
        {
            string? text = null;

            await reader.ReadDotBlockAsync(
                buffer =>
                {
                    text = Encoding.ASCII.GetString(buffer.ToArray());
                    return Task.CompletedTask;
                },
                maxMessageSizeOptions,
                Cancellation);

            return text;
        }

        /// <summary>
        /// A client that keeps sending message content up to the given amount and never sends the terminating dot.
        /// </summary>
        sealed class EndlessContentStream : Stream
        {
            readonly long _limit;

            public EndlessContentStream(long limit)
            {
                _limit = limit;
            }

            public long BytesProduced { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var length = (int)Math.Min(count, _limit - BytesProduced);

                Array.Fill(buffer, (byte)'A', offset, length);

                BytesProduced += length;

                return length;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var length = (int)Math.Min(buffer.Length, _limit - BytesProduced);

                buffer.Span.Slice(0, length).Fill((byte)'A');

                BytesProduced += length;

                return new ValueTask<int>(length);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
