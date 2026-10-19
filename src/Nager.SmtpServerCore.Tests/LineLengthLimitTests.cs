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
    /// <summary>
    /// Verifies that <c>ReadLineAsync</c> enforces the maximum line length and that
    /// a client cannot make the server buffer an unbounded line in memory.
    /// </summary>
    public sealed class LineLengthLimitTests
    {
        const long SixtyFourMegabytes = 64L * 1024 * 1024;
        const long OneMegabyte = 1024 * 1024;
        const int NoLineLengthLimit = 0;

        static readonly MaxMessageSizeOptions NoMessageSizeLimit = new MaxMessageSizeOptions();
        static CancellationToken Cancellation => TestContext.Current.CancellationToken;

        [Fact]
        public async Task WithoutLimit_EverythingTheClientSendsIsBuffered()
        {
            // documents the behaviour before the limit existed: a single 64 MB "line" without CRLF ends up in memory
            var client = ClientSendingUnterminatedLineOf(SixtyFourMegabytes);

            await ReadLineDiscardingContentAsync(client.Reader, NoLineLengthLimit);

            Assert.Equal(SixtyFourMegabytes, client.BytesSent);
        }

        [Fact]
        public async Task UnterminatedLine_IsRejectedBeforeMuchDataIsConsumed()
        {
            var client = ClientSendingUnterminatedLineOf(SixtyFourMegabytes);
            const int maxLineLength = 1000;

            var exception = await Assert.ThrowsAsync<SmtpResponseException>(
                () => ReadLineDiscardingContentAsync(client.Reader, maxLineLength));

            Assert.True(exception.IsQuitRequested, "the session must be closed");
            Assert.Equal(SmtpReplyCode.CommandUnrecognized, exception.Response.ReplyCode);
            Assert.True(client.BytesSent < OneMegabyte,
                $"the server consumed {client.BytesSent} bytes for a line that is limited to {maxLineLength} bytes");
        }

        [Fact]
        public async Task LineExactlyAtTheLimit_IsAccepted()
        {
            var reader = ReaderFor(new string('A', 100) + "\r\n");

            var line = await ReadLineAsync(reader, maxLineLength: 100);

            Assert.Equal(100, line.Length);
        }

        [Fact]
        public async Task TerminatedLineOverTheLimit_IsRejectedWithLineTooLong()
        {
            var reader = ReaderFor(new string('A', 101) + "\r\n");

            var exception = await Assert.ThrowsAsync<SmtpResponseException>(
                () => ReadLineAsync(reader, maxLineLength: 100));

            Assert.True(exception.IsQuitRequested);
            Assert.Same(SmtpResponse.LineTooLong, exception.Response);
        }

        [Fact]
        public async Task Limit_AppliesPerLine_NotToTheWholeBufferedInput()
        {
            // five pipelined lines of 100 bytes (500 in total) must all pass a limit of 150 bytes per line
            var reader = ReaderFor(string.Concat(
                new string('1', 100), "\r\n",
                new string('2', 100), "\r\n",
                new string('3', 100), "\r\n",
                new string('4', 100), "\r\n",
                new string('5', 100), "\r\n"));

            for (var i = 1; i <= 5; i++)
            {
                var line = await ReadLineAsync(reader, maxLineLength: 150);

                Assert.Equal(new string((char)('0' + i), 100), line);
            }
        }

        [Fact]
        public async Task DotBlock_IsNotLimitedByTheCommandLineLength()
        {
            var content = "Subject: test\r\n\r\n" + new string('B', 20000) + "\r\n";
            var reader = ReaderFor(content + ".\r\n");

            var text = await ReadDotBlockAsync(reader);

            Assert.Equal(content.TrimEnd('\r', '\n'), text);
        }

        // ----- test infrastructure -----

        static PipeReader ReaderFor(string clientInput)
            => PipeReader.Create(new MemoryStream(Encoding.ASCII.GetBytes(clientInput)));

        static UnterminatedLineClient ClientSendingUnterminatedLineOf(long bytes)
            => new UnterminatedLineClient(bytes);

        /// <summary>Reads one line and returns it as text.</summary>
        static async Task<string> ReadLineAsync(PipeReader reader, int maxLineLength)
            => await reader.ReadLineAsync(Encoding.ASCII, NoMessageSizeLimit, maxLineLength, Cancellation);

        /// <summary>Reads one line without materializing it, for inputs too large to turn into a string.</summary>
        static async Task ReadLineDiscardingContentAsync(PipeReader reader, int maxLineLength)
            => await reader.ReadLineAsync(_ => Task.CompletedTask, NoMessageSizeLimit, maxLineLength, Cancellation);

        /// <summary>Reads the message content up to the terminating "." and returns it as text.</summary>
        static async Task<string?> ReadDotBlockAsync(PipeReader reader)
        {
            string? text = null;

            await reader.ReadDotBlockAsync(
                buffer =>
                {
                    text = Encoding.ASCII.GetString(buffer.ToArray());
                    return Task.CompletedTask;
                },
                NoMessageSizeLimit,
                Cancellation);

            return text;
        }

        /// <summary>
        /// A client that keeps sending 'A' bytes up to the given amount and never sends a CRLF.
        /// <see cref="BytesSent"/> tells how much of it the server has actually pulled from the connection.
        /// </summary>
        sealed class UnterminatedLineClient
        {
            readonly EndlessLineStream _stream;

            public UnterminatedLineClient(long bytes)
            {
                _stream = new EndlessLineStream(bytes);
                Reader = PipeReader.Create(_stream);
            }

            public PipeReader Reader { get; }

            public long BytesSent => _stream.BytesProduced;
        }

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
    }

    /// <summary>
    /// Verifies the <c>MaxLineLength</c> option of <see cref="SmtpServerOptionsBuilder"/>.
    /// </summary>
    public sealed class MaxLineLengthOptionTests
    {
        [Fact]
        public void Default_SatisfiesTheRfcMinimum()
        {
            var options = new SmtpServerOptionsBuilder().Build();

            Assert.Equal(SmtpServerOptionsBuilder.DefaultMaxLineLength, options.MaxLineLength);
            Assert.True(options.MaxLineLength >= 512, "RFC 5321 requires at least 512 bytes for a command line");
        }

        [Fact]
        public void CanBeConfigured()
        {
            var options = new SmtpServerOptionsBuilder().MaxLineLength(2048).Build();

            Assert.Equal(2048, options.MaxLineLength);
        }

        [Fact]
        public void MustBePositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SmtpServerOptionsBuilder().MaxLineLength(0));
        }
    }
}
