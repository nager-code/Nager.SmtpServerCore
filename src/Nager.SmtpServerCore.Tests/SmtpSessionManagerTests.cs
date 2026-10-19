using Nager.SmtpServerCore.ComponentModel;
using Nager.SmtpServerCore.IO;
using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    /// <summary>
    /// Verifies that <see cref="SmtpSessionManager"/> hands out session slots correctly and
    /// cleans up sessions that never got one.
    /// </summary>
    public sealed class SmtpSessionManagerTests
    {
        static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task WaitingSession_IsCancelledAndDisposed_WhenItsTokenIsCancelled()
        {
            await using var server = SmtpServerUnderTest.Create(maxConcurrentSessions: 1);
            var occupant = server.Connect(TestContext.Current.CancellationToken);

            using var shutdown = new CancellationTokenSource();
            var waiting = server.Connect(shutdown.Token);          // has to queue for a slot
            await Task.Delay(250, TestContext.Current.CancellationToken); // give the manager time to park it in the queue

            // the listener is cancelled (e.g. server shutdown) while the client is still queued
            shutdown.Cancel();

            await Task.WhenAny(waiting.Cancelled, waiting.Faulted, Task.Delay(WaitTimeout, TestContext.Current.CancellationToken));

            Assert.False(waiting.Faulted.IsCompleted, $"the session must not fault: {waiting.FaultDescription}");
            Assert.True(waiting.Cancelled.IsCompleted, "SessionCancelled was not raised for the waiting session");
            Assert.True(waiting.IsDisposed, "the pipe (and therefore the TCP connection) of the waiting session was not disposed");

            occupant.Disconnect();
        }

        [Fact]
        public async Task SessionSlot_IsReleased_WhenTheSessionCompletes()
        {
            await using var server = SmtpServerUnderTest.Create(maxConcurrentSessions: 1);
            var first = server.Connect(TestContext.Current.CancellationToken);

            first.Disconnect();
            Assert.True(await CompletesWithinTimeout(first.Completed), "the first session did not complete");

            var second = server.Connect(TestContext.Current.CancellationToken);

            Assert.True(await CompletesWithinTimeout(second.Created), "the slot of the completed session was not released");
            Assert.True(first.IsDisposed);

            second.Disconnect();
        }

        // ----- test infrastructure -----

        static async Task<bool> CompletesWithinTimeout(Task task)
            => await Task.WhenAny(task, Task.Delay(WaitTimeout, TestContext.Current.CancellationToken)) == task;

        /// <summary>
        /// An <see cref="SmtpServer"/> with its <see cref="SmtpSessionManager"/>. Clients are connected
        /// with <see cref="Connect"/>; disposing waits for all sessions to finish.
        /// </summary>
        sealed class SmtpServerUnderTest : IAsyncDisposable
        {
            readonly ISmtpServerOptions _options;
            readonly ServiceProvider _serviceProvider = new ServiceProvider();
            readonly IEndpointDefinition _endpointDefinition = new EndpointDefinitionBuilder().Build();
            readonly SmtpSessionManager _manager;
            readonly ConcurrentDictionary<SmtpSessionContext, ConnectedClient> _clients = new();

            SmtpServerUnderTest(int maxConcurrentSessions)
            {
                _options = new SmtpServerOptionsBuilder()
                    .ServerName("localhost")
                    .MaxConcurrentSessions(maxConcurrentSessions)
                    .Build();

                var server = new SmtpServer(_options, _serviceProvider);
                _manager = new SmtpSessionManager(server, maxConcurrentSessions);

                // route the server events to the client they belong to
                server.SessionCreated += (_, args) => ClientOf(args.Context)?.OnCreated();
                server.SessionCompleted += (_, args) => ClientOf(args.Context)?.OnCompleted();
                server.SessionCancelled += (_, args) => ClientOf(args.Context)?.OnCancelled();
                server.SessionFaulted += (_, args) => ClientOf(args.Context)?.OnFaulted(args.Exception);
            }

            public static SmtpServerUnderTest Create(int maxConcurrentSessions) => new(maxConcurrentSessions);

            /// <summary>
            /// Connects a client that never sends anything and hands its session to the manager.
            /// </summary>
            public ConnectedClient Connect(CancellationToken cancellationToken = default)
            {
                var pipe = new ClientPipe();
                var context = new SmtpSessionContext(_serviceProvider, _options, _endpointDefinition) { Pipe = pipe };
                var client = new ConnectedClient(pipe);

                _clients[context] = client;
                _manager.Run(context, cancellationToken);

                return client;
            }

            ConnectedClient? ClientOf(ISessionContext context)
                => context is SmtpSessionContext smtpContext && _clients.TryGetValue(smtpContext, out var client) ? client : null;

            public async ValueTask DisposeAsync()
            {
                await Task.WhenAny(_manager.WaitAsync(), Task.Delay(WaitTimeout, TestContext.Current.CancellationToken));
            }
        }

        /// <summary>
        /// A connected client together with the lifecycle events the server raised for its session.
        /// </summary>
        sealed class ConnectedClient
        {
            readonly ClientPipe _pipe;
            readonly TaskCompletionSource<bool> _created = NewCompletionSource<bool>();
            readonly TaskCompletionSource<bool> _completed = NewCompletionSource<bool>();
            readonly TaskCompletionSource<bool> _cancelled = NewCompletionSource<bool>();
            readonly TaskCompletionSource<Exception> _faulted = NewCompletionSource<Exception>();

            public ConnectedClient(ClientPipe pipe)
            {
                _pipe = pipe;
            }

            /// <summary>Completes when the server raised SessionCreated for this client.</summary>
            public Task Created => _created.Task;

            /// <summary>Completes when the server raised SessionCompleted for this client.</summary>
            public Task Completed => _completed.Task;

            /// <summary>Completes when the server raised SessionCancelled for this client.</summary>
            public Task Cancelled => _cancelled.Task;

            /// <summary>Completes with the exception when the server raised SessionFaulted for this client.</summary>
            public Task<Exception> Faulted => _faulted.Task;

            public bool IsDisposed => _pipe.IsDisposed;

            public string? FaultDescription => Faulted.IsCompletedSuccessfully ? Faulted.Result.ToString() : null;

            /// <summary>
            /// Simulates the client closing the connection.
            /// </summary>
            public void Disconnect() => _pipe.CloseFromClient();

            internal void OnCreated() => _created.TrySetResult(true);
            internal void OnCompleted() => _completed.TrySetResult(true);
            internal void OnCancelled() => _cancelled.TrySetResult(true);
            internal void OnFaulted(Exception exception) => _faulted.TrySetResult(exception);

            static TaskCompletionSource<T> NewCompletionSource<T>()
                => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

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

            public void CloseFromClient()
            {
                _input.Writer.Complete();
            }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }
    }
}
