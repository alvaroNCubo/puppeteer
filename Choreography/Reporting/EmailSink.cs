using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text;
using Puppeteer;

namespace Choreography.Reporting
{
	public sealed class EmailSink
	{
		private readonly SmtpSettings smtp;
		private readonly IReadOnlyList<string> recipients;

		public EmailSink(SmtpSettings smtp, IReadOnlyList<string> recipients)
		{
			ArgumentNullException.ThrowIfNull(smtp);
			ArgumentNullException.ThrowIfNull(recipients);
			if (recipients.Count == 0) throw new ArgumentException("At least one recipient required", nameof(recipients));

			this.smtp = smtp;
			this.recipients = recipients;
		}

		public void Send(ActorExecutionError error)
		{
			ArgumentNullException.ThrowIfNull(error);

			try
			{
				using var message = BuildMessage(error);
				using var client = BuildClient();
				client.Send(message);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[EmailSink] Failed to send error email: {ex.Message}");
			}
		}

		private MailMessage BuildMessage(ActorExecutionError error)
		{
			var message = new MailMessage
			{
				From = new MailAddress(smtp.From),
				Subject = BuildSubject(error),
				Body = BuildBody(error),
				IsBodyHtml = false
			};

			foreach (var recipient in recipients)
				message.To.Add(recipient);

			return message;
		}

		private SmtpClient BuildClient()
		{
			return new SmtpClient(smtp.Host, smtp.Port)
			{
				EnableSsl = smtp.UseSsl,
				Credentials = new NetworkCredential(smtp.Username, smtp.Password)
			};
		}

		private static string BuildSubject(ActorExecutionError error)
		{
			var kind = error.IsQuery ? "Qry" : "Cmd";
			return $"[{error.ActorName}|{kind}] {error.Exception.GetType().Name}: {Truncate(error.Exception.Message, 80)}";
		}

		private static string BuildBody(ActorExecutionError error)
		{
			var sb = new StringBuilder();
			sb.AppendLine($"Actor    : {error.ActorName}");
			sb.AppendLine($"Kind     : {(error.IsQuery ? "Query" : "Command")}");
			sb.AppendLine($"Timestamp: {error.Timestamp:O}");
			sb.AppendLine($"Host     : {Dns.GetHostName()}");
			sb.AppendLine();
			sb.AppendLine("Script:");
			sb.AppendLine(error.Script);
			sb.AppendLine();
			sb.AppendLine("Parameters:");
			AppendParameters(sb, error.Parameters);
			sb.AppendLine();
			sb.AppendLine($"ErrorType : {error.Exception.GetType().FullName}");
			sb.AppendLine($"Message   : {error.Exception.Message}");
			sb.AppendLine();
			sb.AppendLine("StackTrace:");
			sb.AppendLine(error.Exception.StackTrace);

			if (error.Exception.InnerException != null)
			{
				sb.AppendLine();
				sb.AppendLine("InnerException:");
				sb.AppendLine(error.Exception.InnerException.ToString());
			}

			return sb.ToString();
		}

		private static void AppendParameters(StringBuilder sb, Parameters parameters)
		{
			bool any = false;
			foreach (var p in parameters)
			{
				sb.Append("  - ").AppendLine(p.ToString());
				any = true;
			}
			if (!any) sb.AppendLine("  (none)");
		}

		private static string Truncate(string value, int maxLength)
		{
			if (value == null) return string.Empty;
			return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
		}
	}
}
