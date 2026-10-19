using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Tests.Mocks;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    /// <summary>
    /// Verifies the STARTTLS command over a real TCP connection on the IPv4 loopback.
    /// </summary>
    public sealed class StartTlsCommandTests
    {
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        static CancellationToken Cancellation => TestContext.Current.CancellationToken;

        [Fact]
        public async Task CommandsPipelinedBeforeTheHandshake_AreNotExecuted()
        {
            await using var session = await TlsSession.StartAsync();

            await session.SendAsync("EHLO client");
            Assert.StartsWith("250", await session.ReadReplyAsync());

            // a man in the middle could append commands to the plain text STARTTLS, they must not end up in the secured session
            await session.SendAsync("STARTTLS\r\nNOOP");
            Assert.StartsWith("220", await session.ReadReplyAsync());

            await session.UpgradeAsync();

            await session.SendAsync("QUIT");
            Assert.StartsWith("221", await session.ReadReplyAsync());

            await session.CompleteAsync();
            Assert.Equal(new[] { "EHLO", "STARTTLS", "QUIT" }, session.ExecutedCommands);
        }

        [Fact]
        public async Task AfterTheHandshake_EhloIsRequiredAgain()
        {
            await using var session = await TlsSession.StartAsync();

            await session.SendAsync("EHLO client");
            Assert.StartsWith("250", await session.ReadReplyAsync());
            await session.SendAsync("STARTTLS");
            Assert.StartsWith("220", await session.ReadReplyAsync());

            await session.UpgradeAsync();

            await session.SendAsync("MAIL FROM:<sender@example.com>");
            Assert.StartsWith("5", await session.ReadReplyAsync());

            await session.SendAsync("EHLO client");
            Assert.StartsWith("250", await session.ReadReplyAsync());
            await session.SendAsync("MAIL FROM:<sender@example.com>");
            Assert.StartsWith("250", await session.ReadReplyAsync());
        }

        [Fact]
        public async Task AfterTheHandshake_ThePlainTextAuthenticationIsDiscarded()
        {
            await using var session = await TlsSession.StartAsync();

            await session.SendAsync("EHLO client");
            Assert.StartsWith("250", await session.ReadReplyAsync());
            await session.SendAsync("AUTH PLAIN " + Convert.ToBase64String(Encoding.ASCII.GetBytes("\0test\01234")));
            Assert.StartsWith("235", await session.ReadReplyAsync());

            await session.SendAsync("STARTTLS");
            Assert.StartsWith("220", await session.ReadReplyAsync());

            await session.UpgradeAsync();

            await session.SendAsync("EHLO client");
            var ehlo = await session.ReadReplyAsync();
            Assert.StartsWith("250", ehlo);
            Assert.DoesNotContain("STARTTLS", ehlo);
            Assert.False(session.Context.Authentication.IsAuthenticated, "the authentication of the plain text session must be discarded");

            await session.SendAsync("AUTH PLAIN " + Convert.ToBase64String(Encoding.ASCII.GetBytes("\0test\01234")));
            Assert.StartsWith("235", await session.ReadReplyAsync());
        }

        /// <summary>
        /// A server side SMTP session and the client end of its connection.
        /// </summary>
        sealed class TlsSession : IAsyncDisposable
        {
            readonly TcpClient _client;
            readonly Task _sessionTask;
            readonly CancellationTokenSource _sessionCancellation = new CancellationTokenSource();
            readonly ConcurrentQueue<string> _executedCommands = new ConcurrentQueue<string>();
            Stream _stream;

            TlsSession(TcpClient client, TcpClient server)
            {
                _client = client;
                _stream = client.GetStream();

                var serviceProvider = new ServiceProvider();
                serviceProvider.Add(new MockUserAuthenticator(username: "test", password: "1234"));

                var options = new SmtpServerOptionsBuilder().ServerName("localhost").Build();
                var endpoint = new EndpointDefinitionBuilder()
                    .Certificate(SmtpServerTests.CreateCertificate())
                    .AllowUnsecureAuthentication()
                    .Build();

                Context = new SmtpSessionContext(serviceProvider, options, endpoint)
                {
                    Pipe = new SecurableDuplexPipe(server.GetStream(), server.Dispose)
                };
                Context.CommandExecuting += (_, args) => _executedCommands.Enqueue(args.Command.Name);

                _sessionTask = new SmtpSession(Context).RunAsync(_sessionCancellation.Token);
            }

            public static async Task<TlsSession> StartAsync()
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();

                var client = new TcpClient();
                var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Cancellation);
                var server = await listener.AcceptTcpClientAsync(Cancellation);
                await connect;

                var session = new TlsSession(client, server);
                Assert.StartsWith("220", await session.ReadReplyAsync());

                return session;
            }

            public SmtpSessionContext Context { get; }

            public string[] ExecutedCommands => _executedCommands.ToArray();

            public async Task SendAsync(string command)
            {
                await _stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), Cancellation);
                await _stream.FlushAsync(Cancellation);
            }

            /// <summary>
            /// Reads a (multiline) reply byte by byte, so nothing that follows it is buffered before a TLS upgrade.
            /// </summary>
            public async Task<string> ReadReplyAsync()
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
                timeout.CancelAfter(Timeout);

                var reply = new StringBuilder();
                var line = new StringBuilder();
                var buffer = new byte[1];

                while (await _stream.ReadAsync(buffer, timeout.Token) == 1)
                {
                    line.Append((char)buffer[0]);

                    if (line.Length >= 2 && line[line.Length - 2] == '\r' && line[line.Length - 1] == '\n')
                    {
                        reply.Append(line);

                        // the last line of a reply has a space after the reply code
                        if (line.Length > 3 && line[3] == ' ')
                        {
                            break;
                        }

                        line.Clear();
                    }
                }

                return reply.ToString();
            }

            public async Task UpgradeAsync()
            {
                var sslStream = new SslStream(_stream, false, (_, _, _, _) => true);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, Cancellation);

                _stream = sslStream;
            }

            public Task CompleteAsync() => _sessionTask.WaitAsync(Timeout, Cancellation);

            public async ValueTask DisposeAsync()
            {
                _sessionCancellation.Cancel();

                try
                {
                    await _sessionTask.WaitAsync(Timeout);
                }
                catch (Exception) { }

                Context.Pipe.Dispose();
                _stream.Dispose();
                _client.Dispose();
                _sessionCancellation.Dispose();
            }
        }
    }
}
