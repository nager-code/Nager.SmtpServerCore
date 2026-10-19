using Nager.SmtpServerCore.Authentication;
using Nager.SmtpServerCore.IO;
using Nager.SmtpServerCore.Protocol;
using Nager.SmtpServerCore.Tests.Mocks;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    /// <summary>
    /// Verifies that the AUTH command and the EHLO advertisement agree on whether authentication is available,
    /// regardless of whether the authenticator is registered through the built-in ServiceProvider or through a
    /// custom IServiceProvider (for example Microsoft.Extensions.DependencyInjection) that only knows IUserAuthenticator.
    /// </summary>
    public sealed class AuthCommandServiceResolutionTests
    {
        /// <summary>
        /// Simulates a DI container that has only an IUserAuthenticator registered (no IUserAuthenticatorFactory).
        /// </summary>
        sealed class AuthenticatorOnlyServiceProvider : IServiceProvider
        {
            readonly IUserAuthenticator _userAuthenticator;

            public AuthenticatorOnlyServiceProvider(IUserAuthenticator userAuthenticator)
            {
                _userAuthenticator = userAuthenticator;
            }

            public object GetService(Type serviceType)
            {
                return serviceType == typeof(IUserAuthenticator) ? _userAuthenticator : null;
            }
        }

        sealed class EmptyServiceProvider : IServiceProvider
        {
            public object GetService(Type serviceType) => null;
        }

        // "test\0test\01234" base64 encoded, matches MockUserAuthenticator("test", "1234")
        const string PlainCredentials = "dGVzdAB0ZXN0ADEyMzQ=";

        static (SmtpSessionContext Context, MemoryStream Output) CreateContext(IServiceProvider serviceProvider)
        {
            var stream = new MemoryStream();

            var context = new SmtpSessionContext(
                serviceProvider,
                new SmtpServerOptionsBuilder().ServerName("localhost").Build(),
                new EndpointDefinitionBuilder().AllowUnsecureAuthentication().Build())
            {
                Pipe = new SecurableDuplexPipe(stream, () => { })
            };

            return (context, stream);
        }

        static string ReadOutput(MemoryStream stream) => Encoding.ASCII.GetString(stream.ToArray());

        [Fact]
        public async Task AuthSucceedsWhenOnlyIUserAuthenticatorIsRegisteredInACustomServiceProvider()
        {
            // arrange
            var serviceProvider = new AuthenticatorOnlyServiceProvider(new MockUserAuthenticator("test", "1234"));
            var (context, output) = CreateContext(serviceProvider);

            // act
            var successful = await new AuthCommand(AuthenticationMethod.Plain, PlainCredentials).ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.True(successful, $"AUTH failed, server replied: {ReadOutput(output).Trim()}");
            Assert.StartsWith("235 ", ReadOutput(output));
            Assert.True(context.Authentication.IsAuthenticated);
            Assert.Equal("test", context.Authentication.User);
        }

        [Fact]
        public async Task EhloAndAuthAgreeWhenOnlyIUserAuthenticatorIsRegisteredInACustomServiceProvider()
        {
            // arrange
            var serviceProvider = new AuthenticatorOnlyServiceProvider(new MockUserAuthenticator("test", "1234"));
            var (context, output) = CreateContext(serviceProvider);

            // act
            await new EhloCommand("client.example.com").ExecuteAsync(context, CancellationToken.None);
            var ehloResponse = ReadOutput(output);

            output.SetLength(0);
            var authSuccessful = await new AuthCommand(AuthenticationMethod.Plain, PlainCredentials).ExecuteAsync(context, CancellationToken.None);
            var authResponse = ReadOutput(output);

            // assert: whatever EHLO advertises, AUTH has to honour
            Assert.Contains("AUTH PLAIN LOGIN", ehloResponse);
            Assert.True(authSuccessful, $"EHLO advertised AUTH but the AUTH command was rejected with: {authResponse.Trim()}");
        }

        [Fact]
        public async Task AuthSucceedsWhenIUserAuthenticatorIsRegisteredInTheBuiltInServiceProvider()
        {
            // arrange
            var serviceProvider = new ComponentModel.ServiceProvider();
            serviceProvider.Add(new MockUserAuthenticator("test", "1234"));
            var (context, _) = CreateContext(serviceProvider);

            // act
            var successful = await new AuthCommand(AuthenticationMethod.Plain, PlainCredentials).ExecuteAsync(context, CancellationToken.None);

            // assert
            Assert.True(successful);
        }

        [Fact]
        public async Task AuthIsNotImplementedAndNotAdvertisedWithoutAnyAuthenticator()
        {
            // arrange
            var (context, output) = CreateContext(new EmptyServiceProvider());

            // act
            await new EhloCommand("client.example.com").ExecuteAsync(context, CancellationToken.None);
            var ehloResponse = ReadOutput(output);

            output.SetLength(0);
            var authSuccessful = await new AuthCommand(AuthenticationMethod.Plain, PlainCredentials).ExecuteAsync(context, CancellationToken.None);
            var authResponse = ReadOutput(output);

            // assert
            Assert.DoesNotContain("AUTH", ehloResponse);
            Assert.False(authSuccessful);
            Assert.StartsWith("502 ", authResponse);
        }
    }
}
