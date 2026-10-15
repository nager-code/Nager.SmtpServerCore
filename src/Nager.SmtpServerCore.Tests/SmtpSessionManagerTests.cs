using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using System;
using System.IO.Pipelines;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class SmtpSessionManagerTests
    {
        /// <summary>
        /// An in-memory pipe that represents a connected client which never sends anything.
        /// </summary>
        sealed class ClientPipe : ISecurableDuplexPipe
        {
            readonly Pipe _input = new Pipe();
            readonly Pipe _output = new Pipe();

            public PipeReader Input => _input.Reader;

            public PipeWriter Output => _output.Writer;

            public bool IsSecure => false;

            public SslProtocols SslProtocol => SslProtocols.None;

            public bool IsDisposed { get; private set; }

            public Task UpgradeAsync(X509Certificate certificate, SslProtocols protocols, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <summary>
            /// Simulates the client closing the connection.
            /// </summary>
            public void CloseFromClient()
            {
                _input.Writer.Complete();
            }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }

        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        static (SmtpServer Server, SmtpSessionManager Manager, Func<ClientPipe, SmtpSessionContext> CreateContext) CreateManager(int maxConcurrentSessions)
        {
            var options = new SmtpServerOptionsBuilder()
                .ServerName("localhost")
                .MaxConcurrentSessions(maxConcurrentSessions)
                .Build();

            var serviceProvider = new ServiceProvider();
            var endpointDefinition = new EndpointDefinitionBuilder().Build();

            var server = new SmtpServer(options, serviceProvider);
            var manager = new SmtpSessionManager(server, maxConcurrentSessions);

            return (server, manager, pipe => new SmtpSessionContext(serviceProvider, options, endpointDefinition) { Pipe = pipe });
        }

        [Fact]
        public async Task CancelledSessionThatIsWaitingForAFreeSlotIsCleanedUp()
        {
            // arrange: a server that allows a single session, the first client occupies the only slot
            var (server, manager, createContext) = CreateManager(maxConcurrentSessions: 1);

            var firstClient = new ClientPipe();
            manager.Run(createContext(firstClient), CancellationToken.None);

            // the second client has to wait for a free slot
            var secondClient = new ClientPipe();
            var secondContext = createContext(secondClient);

            var sessionCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionFaulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

            server.SessionCancelled += (_, args) => { if (args.Context == secondContext) sessionCancelled.TrySetResult(true); };
            server.SessionFaulted += (_, args) => { if (args.Context == secondContext) sessionFaulted.TrySetResult(args.Exception); };

            using var cancellationTokenSource = new CancellationTokenSource();
            manager.Run(secondContext, cancellationTokenSource.Token);

            await Task.Delay(250, TestContext.Current.CancellationToken);

            // act: the server is shut down (or the listener cancelled) while the second client is still waiting
            cancellationTokenSource.Cancel();

            await Task.WhenAny(sessionCancelled.Task, sessionFaulted.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));

            // assert
            Assert.False(sessionFaulted.Task.IsCompleted, $"the session must not fault: {(sessionFaulted.Task.IsCompleted ? sessionFaulted.Task.Result : null)}");
            Assert.True(sessionCancelled.Task.IsCompleted, "SessionCancelled was not raised for the waiting session");
            Assert.True(secondClient.IsDisposed, "the pipe (and therefore the TCP connection) of the waiting session was not disposed");

            // cleanup
            firstClient.CloseFromClient();
            await Task.WhenAny(manager.WaitAsync(), Task.Delay(Timeout, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task SessionSlotIsReleasedWhenTheSessionCompletes()
        {
            // arrange
            var (server, manager, createContext) = CreateManager(maxConcurrentSessions: 1);

            var firstClient = new ClientPipe();
            var firstContext = createContext(firstClient);

            var firstCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionCompleted += (_, args) => { if (args.Context == firstContext) firstCompleted.TrySetResult(true); };

            manager.Run(firstContext, CancellationToken.None);

            // act: the first client disconnects, the second client must be able to start
            firstClient.CloseFromClient();
            await Task.WhenAny(firstCompleted.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));
            Assert.True(firstCompleted.Task.IsCompleted, "the first session did not complete");

            var secondClient = new ClientPipe();
            var secondContext = createContext(secondClient);

            var secondCreated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionCreated += (_, args) => { if (args.Context == secondContext) secondCreated.TrySetResult(true); };

            manager.Run(secondContext, CancellationToken.None);
            await Task.WhenAny(secondCreated.Task, Task.Delay(Timeout, TestContext.Current.CancellationToken));

            // assert
            Assert.True(secondCreated.Task.IsCompleted, "the slot of the completed session was not released");
            Assert.True(firstClient.IsDisposed);

            // cleanup
            secondClient.CloseFromClient();
            await Task.WhenAny(manager.WaitAsync(), Task.Delay(Timeout, TestContext.Current.CancellationToken));
        }
    }
}
