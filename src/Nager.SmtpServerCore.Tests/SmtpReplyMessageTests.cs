using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Protocol;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    /// <summary>
    /// The reply text is visible to every connecting client and ends up in their logs and error messages.
    /// </summary>
    public sealed class SmtpReplyMessageTests
    {
        sealed class SilentClientPipe : ISecurableDuplexPipe
        {
            readonly Pipe _input = new Pipe();
            readonly Pipe _output = new Pipe();

            public PipeReader Input => _input.Reader;
            public PipeWriter Output => _output.Writer;
            public bool IsSecure => false;
            public SslProtocols SslProtocol => SslProtocols.None;

            public Task UpgradeAsync(X509Certificate certificate, SslProtocols protocols, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async Task SendAsync(string text)
            {
                await _input.Writer.WriteAsync(Encoding.ASCII.GetBytes(text));
            }

            public async Task<string> ReadOutputAsync()
            {
                _output.Writer.Complete();

                var result = await _output.Reader.ReadAsync();
                var text = Encoding.ASCII.GetString(result.Buffer.ToArray());

                _output.Reader.AdvanceTo(result.Buffer.End);

                return text;
            }

            public void Dispose() { }
        }

        [Fact]
        public void MaxMessageSizeExceededReplyIsSpelledCorrectly()
        {
            Assert.DoesNotContain("maximium", SmtpResponse.MaxMessageSizeExceeded.Message);
            Assert.Contains("maximum", SmtpResponse.MaxMessageSizeExceeded.Message);
        }

        [Fact]
        public void TransactionFailedReplyHasAMessage()
        {
            Assert.False(string.IsNullOrWhiteSpace(SmtpResponse.TransactionFailed.Message), "554 without any text is not helpful for the client");
        }

        [Fact]
        public async Task ReplyWithoutMessageDoesNotEndWithATrailingSpace()
        {
            // arrange
            var stream = new MemoryStream();
            var writer = PipeWriter.Create(stream);

            // act
            await writer.WriteReplyAsync(new SmtpResponse(SmtpReplyCode.TransactionFailed), TestContext.Current.CancellationToken);

            // assert
            Assert.Equal("554\r\n", Encoding.ASCII.GetString(stream.ToArray()));
        }

        [Fact]
        public async Task CancelledSessionReplyIsSpelledCorrectly()
        {
            // arrange: a client that starts a message and then stalls while sending the content
            var pipe = new SilentClientPipe();

            await pipe.SendAsync("EHLO client.example.com\r\nMAIL FROM:<sender@example.com>\r\nRCPT TO:<recipient@example.com>\r\nDATA\r\nSubject: test\r\n");

            var context = new SmtpSessionContext(
                new ServiceProvider(),
                new SmtpServerOptionsBuilder().ServerName("localhost").Build(),
                new EndpointDefinitionBuilder().Build())
            {
                Pipe = pipe
            };

            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            // act: the session is cancelled (e.g. session timeout) while waiting for the rest of the message
            await new SmtpSession(context).RunAsync(cancellationTokenSource.Token);

            var output = await pipe.ReadOutputAsync();

            // assert
            Assert.Contains("554 transaction failed\r\n", output);
            Assert.Contains("221 The session has been cancelled.", output);
            Assert.DoesNotContain("has be cancelled", output);
        }
    }
}
