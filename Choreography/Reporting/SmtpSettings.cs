using System;

namespace Choreography.Reporting
{
	public sealed class SmtpSettings
	{
		public string Host { get; }
		public int Port { get; }
		public string From { get; }
		public string Username { get; }
		public string Password { get; }
		public bool UseSsl { get; }

		public SmtpSettings(string host, int port, string from,
			string username, string password, bool useSsl)
		{
			ArgumentNullException.ThrowIfNull(host);
			ArgumentNullException.ThrowIfNull(from);
			ArgumentNullException.ThrowIfNull(username);
			ArgumentNullException.ThrowIfNull(password);
			if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));

			Host = host;
			Port = port;
			From = from;
			Username = username;
			Password = password;
			UseSsl = useSsl;
		}
	}
}
