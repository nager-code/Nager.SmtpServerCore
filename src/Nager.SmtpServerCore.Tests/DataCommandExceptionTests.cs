using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Mail;
using Nager.SmtpServerCore.Protocol;
using Nager.SmtpServerCore.Storage;
using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class DataCommandExceptionTests
    {
        /// <summary>
        /// A stream that reads from one buffer (the client input) and writes to another (the server output).
        /// </summary>
        sealed class DuplexStream : Stream
        {
            readonly MemoryStream _input;
            readonly MemoryStream _output = new MemoryStream();

            public DuplexStream(string input)
            {
                _input = new MemoryStream(Encoding.ASCII.GetBytes(input));
            }

            public string Output => Encoding.ASCII.GetString(_output.ToArray());

            public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
            public override void Flush() { }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        sealed class ThrowingMessageStore : MessageStore
        {
            readonly Exception _exception;

            public ThrowingMessageStore(Exception exception)
            {
                _exception = exception;
            }

            public override Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
            {
                throw _exception;
            }
        }

        const string MessageContent = "Subject: test\r\n\r\nHello World\r\n";

        static (SmtpSessionContext Context, DuplexStream Stream) CreateContext(string input, IMessageStore messageStore, Action<SmtpServerOptionsBuilder> configure = null)
        {
            var stream = new DuplexStream(input);

            var serviceProvider = new ServiceProvider();
            serviceProvider.Add(messageStore);

            var options = new SmtpServerOptionsBuilder().ServerName("localhost");
            configure?.Invoke(options);

            var context = new SmtpSessionContext(serviceProvider, options.Build(), new EndpointDefinitionBuilder().Build())
            {
                Pipe = new SecurableDuplexPipe(stream, () => { })
            };

            context.Transaction.Reset();
            context.Transaction.From = new Mailbox("sender@example.com");
            context.Transaction.To.Add(new Mailbox("recipient@example.com"));

            return (context, stream);
        }

        [Fact]
        public async Task ResponseExceptionFromMessageStoreReachesTheSession()
        {
            // arrange: a message store that rejects the message with a specific SMTP reply
            var rejection = new SmtpResponseException(new SmtpResponse(SmtpReplyCode.MailboxUnavailable, "rejected by policy"));
            var (context, stream) = CreateContext(MessageContent + ".\r\n", new ThrowingMessageStore(rejection));

            // act
            var exception = await Record.ExceptionAsync(() => new DataCommand().ExecuteAsync(context, CancellationToken.None));

            // assert: the exception must propagate so that SmtpSession can write the reply, otherwise the client
            // never receives an answer to its message content
            Assert.NotNull(exception);
            Assert.Same(rejection, exception);
        }

        [Fact]
        public async Task MaxMessageSizeExceededInStrictModeClosesTheSessionWithAReply()
        {
            // arrange: 50 byte limit, message content of 500 bytes
            var content = "Subject: test\r\n\r\n" + new string('A', 500) + "\r\n";
            var (context, _) = CreateContext(content + ".\r\n", MessageStore.Default, options => options.MaxMessageSize(50, MaxMessageSizeHandling.Strict));

            // act
            var exception = await Record.ExceptionAsync(() => new DataCommand().ExecuteAsync(context, CancellationToken.None));

            // assert
            var responseException = Assert.IsType<SmtpResponseException>(exception);
            Assert.True(responseException.IsQuitRequested, "the session must be closed when the message size is exceeded in strict mode");
            Assert.Same(SmtpResponse.MaxMessageSizeExceeded, responseException.Response);
        }

        [Fact]
        public async Task SessionWritesTheReplyOfAResponseExceptionThrownByTheMessageStore()
        {
            // arrange: a complete SMTP dialogue, the message store rejects the message
            var rejection = new SmtpResponseException(new SmtpResponse(SmtpReplyCode.MailboxUnavailable, "rejected by policy"));

            var dialogue =
                "EHLO client.example.com\r\n" +
                "MAIL FROM:<sender@example.com>\r\n" +
                "RCPT TO:<recipient@example.com>\r\n" +
                "DATA\r\n" +
                MessageContent +
                ".\r\n" +
                "QUIT\r\n";

            var (context, stream) = CreateContext(dialogue, new ThrowingMessageStore(rejection));

            // act
            await new SmtpSession(context).RunAsync(CancellationToken.None);

            // assert: after the terminating "." the client must receive the 550 reply before the 221 for QUIT
            var output = stream.Output;
            var indexOfRejection = output.IndexOf("550 rejected by policy", StringComparison.Ordinal);
            var indexOfQuit = output.IndexOf("221 ", StringComparison.Ordinal);

            Assert.True(indexOfRejection >= 0, $"the 550 reply of the message store is missing in the server output:\r\n{output}");
            Assert.True(indexOfQuit > indexOfRejection, "the 550 reply must be sent before the session is closed");
        }

        [Fact]
        public async Task ResponseOfTheMessageStoreIsWritten()
        {
            // arrange
            var (context, stream) = CreateContext(MessageContent + ".\r\n", MessageStore.Default);

            // act
            var successful = await new DataCommand().ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.True(successful);
            Assert.EndsWith("250 Ok\r\n", stream.Output);
        }

        [Fact]
        public async Task UnexpectedExceptionFromMessageStoreResultsInTransactionFailed()
        {
            // arrange
            var (context, stream) = CreateContext(MessageContent + ".\r\n", new ThrowingMessageStore(new InvalidOperationException("storage is down")));

            // act
            var successful = await new DataCommand().ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.False(successful);
            Assert.Contains("\r\n554 ", stream.Output);
        }
    }
}
