using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Text;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class PipeReaderExtensionTests
    {
        static PipeReader CreatePipeReader(string text)
        {
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

            return PipeReader.Create(stream);
        }

        [Fact]
        // ReSharper disable once InconsistentNaming
        public async Task CanReadLineAndRemoveTrailingCRLF()
        {
            // arrange
            var reader = CreatePipeReader("abcde\r\n");

            var maxMessageSizeOptions = new MaxMessageSizeOptions();

            // act
            var line = await reader.ReadLineAsync(Encoding.ASCII, maxMessageSizeOptions, SmtpServerOptionsBuilder.DefaultMaxLineLength, cancellationToken: TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(5, line.Length);
            Assert.Equal("abcde", line);
        }

        [Fact]
        // ReSharper disable once InconsistentNaming
        public async Task CanReadLinesWithInconsistentCRLF()
        {
            // arrange
            var reader = CreatePipeReader("ab\rcd\ne\r\n");

            var maxMessageSizeOptions = new MaxMessageSizeOptions();

            // act
            var line = await reader.ReadLineAsync(Encoding.ASCII, maxMessageSizeOptions, SmtpServerOptionsBuilder.DefaultMaxLineLength, cancellationToken: TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(7, line.Length);
            Assert.Equal("ab\rcd\ne", line);
        }

        [Fact]
        // ReSharper disable once InconsistentNaming
        public async Task CanReadMultipleLines()
        {
            // arrange
            var reader = CreatePipeReader("abcde\r\nfghij\r\nklmno\r\n");

            var maxMessageSizeOptions = new MaxMessageSizeOptions();

            // act
            var line1 = await reader.ReadLineAsync(Encoding.ASCII, maxMessageSizeOptions, SmtpServerOptionsBuilder.DefaultMaxLineLength, cancellationToken: TestContext.Current.CancellationToken);
            var line2 = await reader.ReadLineAsync(Encoding.ASCII, maxMessageSizeOptions, SmtpServerOptionsBuilder.DefaultMaxLineLength, cancellationToken: TestContext.Current.CancellationToken);
            var line3 = await reader.ReadLineAsync(Encoding.ASCII, maxMessageSizeOptions, SmtpServerOptionsBuilder.DefaultMaxLineLength, cancellationToken: TestContext.Current.CancellationToken);

            // assert
            Assert.Equal("abcde", line1);
            Assert.Equal("fghij", line2);
            Assert.Equal("klmno", line3);
        }

        [Fact]
        public async Task CanReadBlockWithDotStuffingRemoved()
        {
            // arrange
            var reader = CreatePipeReader("abcd\r\n..1234\r\n.\r\n");

            var maxMessageSizeOptions = new MaxMessageSizeOptions();

            // act
            var text = "";
            await reader.ReadDotBlockAsync(buffer =>
            {
                text = StringUtil.Create(buffer);

                return Task.CompletedTask;
            },
            maxMessageSizeOptions,
            cancellationToken: TestContext.Current.CancellationToken);

            // assert
            Assert.Equal("abcd\r\n.1234", text);
        }

        [Theory]
        [InlineData("..1234\r\nabcd\r\n.\r\n", ".1234\r\nabcd")]
        [InlineData("..\r\n.\r\n", ".")]
        [InlineData("...\r\n..\r\n.\r\n", "..\r\n.")]
        public async Task CanReadBlockWithDotStuffingOfTheFirstLineRemoved(string input, string expected)
        {
            // arrange
            var reader = CreatePipeReader(input);

            // act
            var text = await ReadDotBlockAsync(reader);

            // assert
            Assert.Equal(expected, text);
        }

        [Fact]
        public async Task CanReadEmptyBlock()
        {
            // arrange
            var reader = CreatePipeReader(".\r\nQUIT\r\n");

            // act
            var text = await ReadDotBlockAsync(reader);
            var line = await reader.ReadLineAsync(Encoding.ASCII, new MaxMessageSizeOptions(), SmtpServerOptionsBuilder.DefaultMaxLineLength, cancellationToken: TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(string.Empty, text);
            Assert.Equal("QUIT", line);
        }

        [Fact]
        public async Task CanReadEmptyBlockThatArrivesInPieces()
        {
            // arrange
            var pipe = new Pipe();

            // act
            var readTask = ReadDotBlockAsync(pipe.Reader);

            await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes("."), TestContext.Current.CancellationToken);
            await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes("\r"), TestContext.Current.CancellationToken);
            await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes("\n"), TestContext.Current.CancellationToken);

            var text = await readTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // assert
            Assert.Equal(string.Empty, text);
        }

        static async Task<string?> ReadDotBlockAsync(PipeReader reader)
        {
            string? text = null;

            await reader.ReadDotBlockAsync(buffer =>
            {
                text = Encoding.ASCII.GetString(buffer.ToArray());

                return Task.CompletedTask;
            },
            new MaxMessageSizeOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

            return text;
        }
    }
}
