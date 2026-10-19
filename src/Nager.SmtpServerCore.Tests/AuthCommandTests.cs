using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Protocol;
using Nager.SmtpServerCore.Tests.Mocks;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public class AuthCommandTests
    {
        static (SmtpSessionContext Context, FakeClientStream Client) CreateContext(string clientInput = "", bool withUserAuthenticator = true)
        {
            var client = new FakeClientStream(clientInput);

            var serviceProvider = new ServiceProvider();
            if (withUserAuthenticator)
            {
                serviceProvider.Add(new MockUserAuthenticator(username: "test", password: "1234"));
            }

            var smtpServerOptions = new SmtpServerOptionsBuilder().Build();
            var endpointDefinitions = new EndpointDefinitionBuilder().Build();
            var smtpSessionContext = new SmtpSessionContext(serviceProvider, smtpServerOptions, endpointDefinitions)
            {
                Pipe = new SecurableDuplexPipe(client, () => { })
            };

            return (smtpSessionContext, client);
        }

        static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        static string[] Replies(FakeClientStream client)
            => client.ServerOutput.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        [Fact]
        public async Task Test_Login_WithoutUserAuthenticator()
        {
            var (smtpSessionContext, _) = CreateContext(withUserAuthenticator: false);

            var authCommand = new AuthCommand(AuthenticationMethod.Login, "myuser");
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
        }

        [Fact]
        public async Task Test_Login_WithUserAuthenticator()
        {
            var (smtpSessionContext, _) = CreateContext();

            var authCommand = new AuthCommand(AuthenticationMethod.Login, "myuser");
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
        }

        [Fact]
        public async Task Test_Login_WithUserNameAndPasswordOnContinuationLines()
        {
            var (smtpSessionContext, client) = CreateContext($"{Base64("test")}\r\n{Base64("1234")}\r\n");

            var authCommand = new AuthCommand(AuthenticationMethod.Login, null);
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.True(successful);
            Assert.True(smtpSessionContext.Authentication.IsAuthenticated);
            Assert.Equal(new[] { "334 VXNlcm5hbWU6", "334 UGFzc3dvcmQ6" }, Replies(client).Take(2));
            Assert.StartsWith("235 ", Replies(client).Last());
        }

        [Fact]
        public async Task Test_Plain_WithUserAuthenticator()
        {
            var (smtpSessionContext, _) = CreateContext();

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, "dGVzdAB0ZXN0ADEyMzQ=");
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.True(successful);
        }

        [Fact]
        public async Task Test_Plain_WithCredentialsOnContinuationLine()
        {
            var (smtpSessionContext, client) = CreateContext($"{Base64("\0test\01234")}\r\n");

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, null);
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.True(successful);
            Assert.StartsWith("334 ", Replies(client).First());
        }

        [Fact]
        public async Task Test_Plain_WithAuthorizationIdentityEqualToTheUser_IsAccepted()
        {
            var (smtpSessionContext, _) = CreateContext();

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, Base64("test\0test\01234"));
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.True(successful);
            Assert.Equal("test", smtpSessionContext.Authentication.User);
        }

        [Fact]
        public async Task Test_Plain_WithInvalidBase64OnContinuationLine_RepliesOnce()
        {
            var (smtpSessionContext, client) = CreateContext("!!!\r\n");

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, null);
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
            Assert.Equal(2, Replies(client).Length);
            Assert.StartsWith("334 ", Replies(client)[0]);
            Assert.StartsWith("535 ", Replies(client)[1]);
            Assert.Equal(1, smtpSessionContext.AuthenticationAttempts);
        }

        [Fact]
        public async Task Test_Login_WithWrongPassword_CountsAsFailedAttempt()
        {
            var (smtpSessionContext, client) = CreateContext($"{Base64("test")}\r\n{Base64("wrong")}\r\n");

            var authCommand = new AuthCommand(AuthenticationMethod.Login, null);
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
            Assert.False(smtpSessionContext.Authentication.IsAuthenticated);
            Assert.Equal(1, smtpSessionContext.AuthenticationAttempts);
            Assert.StartsWith("535 ", Replies(client).Last());
        }

        [Fact]
        public async Task Test_Plain_WithAuthorizationIdentityOfAnotherUser_IsRejected()
        {
            var (smtpSessionContext, _) = CreateContext();

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, Base64("admin\0test\01234"));
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
            Assert.False(smtpSessionContext.Authentication.IsAuthenticated);
        }

        [Theory]
        [InlineData("\0test\01234\nsuffix")]
        [InlineData("\0test\01234\0suffix")]
        [InlineData("\0test\0\01234")]
        [InlineData("test\01234")]
        public async Task Test_Plain_WithMalformedCredentials_IsRejected(string credentials)
        {
            var (smtpSessionContext, _) = CreateContext();

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, Base64(credentials));
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
            Assert.False(smtpSessionContext.Authentication.IsAuthenticated);
        }

        [Fact]
        public async Task Test_Plain_WithInvalidBase64_RepliesOnceAndCountsAsFailedAttempt()
        {
            var (smtpSessionContext, client) = CreateContext();

            var authCommand = new AuthCommand(AuthenticationMethod.Plain, "!!!");
            var successful = await authCommand.ExecuteAsync(smtpSessionContext, CancellationToken.None);

            Assert.False(successful);
            Assert.Single(Replies(client));
            Assert.StartsWith("535 ", Replies(client)[0]);
            Assert.Equal(1, smtpSessionContext.AuthenticationAttempts);
        }

        [Fact]
        public async Task Test_MaxAuthenticationAttempts_ClosesTheSession()
        {
            var (smtpSessionContext, _) = CreateContext();
            var attempts = smtpSessionContext.ServerOptions.MaxAuthenticationAttempts;
            var wrongPassword = Base64("\0test\0wrong");

            for (var i = 1; i < attempts; i++)
            {
                Assert.False(await new AuthCommand(AuthenticationMethod.Plain, wrongPassword).ExecuteAsync(smtpSessionContext, CancellationToken.None));
            }

            var exception = await Assert.ThrowsAsync<SmtpResponseException>(
                () => new AuthCommand(AuthenticationMethod.Plain, wrongPassword).ExecuteAsync(smtpSessionContext, CancellationToken.None));

            Assert.True(exception.IsQuitRequested, "the session must be closed after the last attempt");
            Assert.Equal(attempts, smtpSessionContext.AuthenticationAttempts);
        }

        /// <summary>
        /// Stands in for the network connection: reads return the scripted client input,
        /// writes are recorded as the server output.
        /// </summary>
        sealed class FakeClientStream : Stream
        {
            readonly MemoryStream _clientInput;
            readonly MemoryStream _serverOutput = new MemoryStream();

            public FakeClientStream(string clientInput)
            {
                _clientInput = new MemoryStream(Encoding.ASCII.GetBytes(clientInput));
            }

            public string ServerOutput => Encoding.ASCII.GetString(_serverOutput.ToArray());

            public override int Read(byte[] buffer, int offset, int count) => _clientInput.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count) => _serverOutput.Write(buffer, offset, count);
            public override void Flush() { }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
