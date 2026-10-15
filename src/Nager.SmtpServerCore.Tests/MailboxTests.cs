using Nager.SmtpServerCore.Mail;
using System;
using Xunit;

namespace Nager.SmtpServerCore.Tests
{
    public sealed class MailboxTests
    {
        [Fact]
        public void CanCreateFromAddress()
        {
            var mailbox = new Mailbox("user@example.com");

            Assert.Equal("user", mailbox.User);
            Assert.Equal("example.com", mailbox.Host);
            Assert.Equal("user@example.com", mailbox.AsAddress());
        }

        [Fact]
        public void CanCreateFromAddressWithoutHost()
        {
            // RFC 5321 allows a local part without a domain, e.g. "<Postmaster>"
            var mailbox = new Mailbox("postmaster");

            Assert.Equal("postmaster", mailbox.User);
            Assert.Equal(string.Empty, mailbox.Host);
        }

        [Fact]
        public void LastAtSignSeparatesUserAndHost()
        {
            // a quoted local part may itself contain an "@"
            var mailbox = new Mailbox("\"first@last\"@example.com");

            Assert.Equal("\"first@last\"", mailbox.User);
            Assert.Equal("example.com", mailbox.Host);
        }

        [Fact]
        public void CanCreateFromEmptyAddress()
        {
            var mailbox = new Mailbox(string.Empty);

            Assert.Equal(string.Empty, mailbox.User);
            Assert.Equal(string.Empty, mailbox.Host);
            Assert.Null(mailbox.AsAddress());
        }

        [Fact]
        public void NullAddressThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new Mailbox(null));
        }
    }
}
