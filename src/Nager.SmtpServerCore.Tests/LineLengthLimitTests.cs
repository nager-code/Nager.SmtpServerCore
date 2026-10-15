using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Protocol;
using Nager.SmtpServerCore.Text;
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
    public sealed class LineLengthLimitTests
    {
        /// <summary>
        /// A stream that behaves like a client which keeps sending bytes without ever sending a CRLF.
        /// </summary>
        sealed class EndlessLineStream : Stream
        {
            readonly long _limit;

            public EndlessLineStream(long limit)
            {
                _limit = limit;
            }

            public long BytesProduced { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _limit - BytesProduced;

                if (remaining <= 0)
                {
                    return 0;
                }

                var length = (int)Math.Min(count, remaining);

                Array.Fill(buffer, (byte)'A', offset, length);

                BytesProduced += length;

                return length;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var array = new byte[buffer.Length];
                var read = Read(array, 0, array.Length);
                array.AsSpan(0, read).CopyTo(buffer.Span);

                return new ValueTask<int>(read);
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

        static PipeReader CreatePipeReader(string text)
        {
            return PipeReader.Create(new MemoryStream(Encoding.ASCII.GetBytes(text)));
        }

        [Fact]
        public async Task ReadLineWithoutLimitBuffersEverythingTheClientSends()
        {
            // arrange: this documents the behaviour before the limit existed (maxLineLength = 0 disables the limit),
            // a single "line" of 64 MB without a CRLF is buffered completely in memory
            const long sixtyFourMegabytes = 64L * 1024 * 1024;

            var stream = new EndlessLineStream(sixtyFourMegabytes);
            var reader = PipeReader.Create(stream);

            // act
            await reader.ReadLineAsync(_ => Task.CompletedTask, new MaxMessageSizeOptions(), maxLineLength: 0, TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(sixtyFourMegabytes, stream.BytesProduced);
        }

        [Fact]
        public async Task ReadLineStopsConsumingDataWhenTheLimitIsExceeded()
        {
            // arrange: a client that would send 64 MB without a CRLF
            const long sixtyFourMegabytes = 64L * 1024 * 1024;
            const int maxLineLength = 1000;

            var stream = new EndlessLineStream(sixtyFourMegabytes);
            var reader = PipeReader.Create(stream);

            // act
            var exception = await Assert.ThrowsAsync<SmtpResponseException>(async () =>
                await reader.ReadLineAsync(_ => Task.CompletedTask, new MaxMessageSizeOptions(), maxLineLength, TestContext.Current.CancellationToken));

            // assert
            Assert.True(exception.IsQuitRequested, "the session must be closed");
            Assert.Equal(SmtpReplyCode.CommandUnrecognized, exception.Response.ReplyCode);
            Assert.True(stream.BytesProduced < 1024 * 1024, $"the server consumed {stream.BytesProduced} bytes for a line that is limited to {maxLineLength} bytes");
        }

        [Fact]
        public async Task ReadLineAcceptsLineThatIsExactlyAtTheLimit()
        {
            // arrange
            var reader = CreatePipeReader(new string('A', 100) + "\r\n");

            // act
            var line = await reader.ReadLineAsync(Encoding.ASCII, new MaxMessageSizeOptions(), maxLineLength: 100, TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(100, line.Length);
        }

        [Fact]
        public async Task ReadLineRejectsTerminatedLineThatExceedsTheLimit()
        {
            // arrange
            var reader = CreatePipeReader(new string('A', 101) + "\r\n");

            // act
            var exception = await Assert.ThrowsAsync<SmtpResponseException>(async () =>
                await reader.ReadLineAsync(Encoding.ASCII, new MaxMessageSizeOptions(), maxLineLength: 100, TestContext.Current.CancellationToken));

            // assert
            Assert.True(exception.IsQuitRequested);
            Assert.Same(SmtpResponse.LineTooLong, exception.Response);
        }

        [Fact]
        public async Task LimitAppliesToEachLineAndNotToTheWholeBufferedInput()
        {
            // arrange: five pipelined lines of 100 bytes, 500 bytes in total, with a limit of 150 bytes per line
            var text = string.Concat(
                new string('1', 100), "\r\n",
                new string('2', 100), "\r\n",
                new string('3', 100), "\r\n",
                new string('4', 100), "\r\n",
                new string('5', 100), "\r\n");

            var reader = CreatePipeReader(text);

            // act & assert
            for (var i = 1; i <= 5; i++)
            {
                var line = await reader.ReadLineAsync(Encoding.ASCII, new MaxMessageSizeOptions(), maxLineLength: 150, TestContext.Current.CancellationToken);

                Assert.Equal(new string((char)('0' + i), 100), line);
            }
        }

        [Fact]
        public async Task ReadDotBlockIsNotLimitedByTheLineLength()
        {
            // arrange: message content with a line that is much longer than the command line limit
            var content = "Subject: test\r\n\r\n" + new string('B', 20000) + "\r\n";
            var reader = CreatePipeReader(content + ".\r\n");

            // act
            string text = null;
            await reader.ReadDotBlockAsync(
                buffer =>
                {
                    text = Encoding.ASCII.GetString(buffer.ToArray());
                    return Task.CompletedTask;
                },
                new MaxMessageSizeOptions(),
                TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(content.TrimEnd('\r', '\n'), text);
        }

        [Fact]
        public void DefaultMaxLineLengthIsConfigured()
        {
            // act
            var options = new SmtpServerOptionsBuilder().Build();

            // assert
            Assert.Equal(SmtpServerOptionsBuilder.DefaultMaxLineLength, options.MaxLineLength);
            Assert.True(options.MaxLineLength >= 512, "RFC 5321 requires at least 512 bytes for a command line");
        }

        [Fact]
        public void MaxLineLengthCanBeConfigured()
        {
            // act
            var options = new SmtpServerOptionsBuilder().MaxLineLength(2048).Build();

            // assert
            Assert.Equal(2048, options.MaxLineLength);
        }

        [Fact]
        public void MaxLineLengthMustBePositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SmtpServerOptionsBuilder().MaxLineLength(0));
        }
    }
}
