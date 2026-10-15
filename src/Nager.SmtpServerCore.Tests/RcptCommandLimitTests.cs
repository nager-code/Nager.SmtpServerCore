using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Mail;
using Nager.SmtpServerCore.Protocol;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class RcptCommandLimitTests
    {
        static (SmtpSessionContext Context, MemoryStream Output) CreateContext(int maxRcptToCount)
        {
            var stream = new MemoryStream();

            var context = new SmtpSessionContext(
                new ServiceProvider(),
                new SmtpServerOptionsBuilder().ServerName("localhost").MaxRcptToCount(maxRcptToCount).Build(),
                new EndpointDefinitionBuilder().Build())
            {
                Pipe = new SecurableDuplexPipe(stream, () => { })
            };

            context.Transaction.Reset();
            context.Transaction.From = new Mailbox("sender@example.com");

            return (context, stream);
        }

        static string LastReply(MemoryStream stream)
        {
            var lines = Encoding.ASCII.GetString(stream.ToArray()).TrimEnd('\r', '\n').Split("\r\n");

            return lines[lines.Length - 1];
        }

        [Fact]
        public async Task AcceptsExactlyMaxRcptToCountRecipients()
        {
            // arrange
            var (context, output) = CreateContext(maxRcptToCount: 2);

            // act
            var first = await new RcptCommand(new Mailbox("first@example.com")).ExecuteAsync(context, CancellationToken.None);
            var second = await new RcptCommand(new Mailbox("second@example.com")).ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.True(first);
            Assert.True(second);
            Assert.Equal(2, context.Transaction.To.Count);
            Assert.StartsWith("250 ", LastReply(output));
        }

        [Fact]
        public async Task RejectsRecipientThatExceedsMaxRcptToCount()
        {
            // arrange
            var (context, output) = CreateContext(maxRcptToCount: 2);

            await new RcptCommand(new Mailbox("first@example.com")).ExecuteAsync(context, CancellationToken.None);
            await new RcptCommand(new Mailbox("second@example.com")).ExecuteAsync(context, CancellationToken.None);

            // act: the third recipient is one more than the configured maximum
            var third = await new RcptCommand(new Mailbox("third@example.com")).ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.False(third, "the third recipient was accepted although MaxRcptToCount is 2");
            Assert.Equal(2, context.Transaction.To.Count);
            Assert.StartsWith("452 ", LastReply(output));
        }

        [Fact]
        public async Task LimitOfOneAllowsASingleRecipient()
        {
            // arrange
            var (context, _) = CreateContext(maxRcptToCount: 1);

            // act
            var first = await new RcptCommand(new Mailbox("first@example.com")).ExecuteAsync(context, CancellationToken.None);
            var second = await new RcptCommand(new Mailbox("second@example.com")).ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.True(first);
            Assert.False(second);
            Assert.Single(context.Transaction.To);
        }

        [Fact]
        public async Task DefaultLimitAcceptsTenRecipients()
        {
            // arrange
            var options = new SmtpServerOptionsBuilder().Build();
            var (context, _) = CreateContext(options.MaxRcptToCount);

            // act
            for (var i = 0; i < options.MaxRcptToCount; i++)
            {
                Assert.True(await new RcptCommand(new Mailbox($"user{i}@example.com")).ExecuteAsync(context, CancellationToken.None));
            }

            var oneTooMany = await new RcptCommand(new Mailbox("extra@example.com")).ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.Equal(10, options.MaxRcptToCount);
            Assert.False(oneTooMany);
            Assert.Equal(options.MaxRcptToCount, context.Transaction.To.Count);
        }
    }
}
