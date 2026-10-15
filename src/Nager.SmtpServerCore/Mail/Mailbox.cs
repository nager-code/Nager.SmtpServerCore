using System;

namespace Nager.SmtpServerCore.Mail
{
    /// <summary>
    /// Mailbox
    /// </summary>
    public sealed class Mailbox : IMailbox
    {
        /// <summary>
        /// Empty Mailbox
        /// </summary>
        public static readonly IMailbox Empty = new Mailbox(string.Empty, string.Empty);

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="user">The user/account name.</param>
        /// <param name="host">The host server.</param>
        public Mailbox(string user, string host)
        {
            User = user;
            Host = host;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="address">The email address to create the mailbox from, an address without a host part (e.g. "postmaster") is allowed.</param>
        public Mailbox(string address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            address = address.Replace(" ", string.Empty);

            // the local part may be a quoted string that itself contains an "@", the host part can not
            var index = address.LastIndexOf('@');

            if (index < 0)
            {
                User = address;
                Host = string.Empty;
                return;
            }

            User = address.Substring(0, index);
            Host = address.Substring(index + 1);
        }

        /// <summary>
        /// Gets the user/account name.
        /// </summary>
        public string User { get; }

        /// <summary>
        /// Gets the host server.
        /// </summary>
        public string Host { get; }
    }
}
