using System.Security.Authentication;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class EndpointDefinitionBuilderTests
    {
        [Fact]
        public void DefaultSupportedSslProtocolsLetsTheOperatingSystemChoose()
        {
            // act
            var endpointDefinition = new EndpointDefinitionBuilder().Build();

            // assert: SslProtocols.None means "use the system default", which enables TLS 1.3 where available
            Assert.Equal(SslProtocols.None, endpointDefinition.SupportedSslProtocols);
        }

        [Fact]
        public void SupportedSslProtocolsCanBeRestricted()
        {
            // act
            var endpointDefinition = new EndpointDefinitionBuilder().SupportedSslProtocols(SslProtocols.Tls12).Build();

            // assert
            Assert.Equal(SslProtocols.Tls12, endpointDefinition.SupportedSslProtocols);
        }
    }
}
