using System;
using System.Collections.Generic;
using System.Text;

namespace Puppeteer.EventSourcing.DB
{
	// Pre-parser ortogonal al backend: extrae la key `localBufferPath=<path>` de
	// cualquier connection string (FS / MySQL / SQLServer / IN_MEMORY) y devuelve
	// el CS saneado (sin esa key) mas el path del buffer si vino. Se quita la key
	// antes de pasar el CS al parser del backend para no contaminar a MySQL/SQLServer
	// (ADO.NET conocen sus propias keys y rechazan/ignoran las ajenas).
	internal static class StorageConnectionString
	{
		internal const string LocalBufferPathKey = "localBufferPath";

		internal static (string sanitizedConnectionString, string localBufferPath) Extract(string connectionString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);

			if (!connectionString.Contains('='))
				return (connectionString, null);

			string localBufferPath = null;
			bool keySeen = false;
			var kept = new List<string>();

			foreach (string segment in connectionString.Split(';'))
			{
				if (string.IsNullOrWhiteSpace(segment))
				{
					kept.Add(segment);
					continue;
				}

				int eq = segment.IndexOf('=');
				if (eq < 0)
				{
					kept.Add(segment);
					continue;
				}

				string key = segment[..eq].Trim();
				string value = segment[(eq + 1)..].Trim();

				if (key.Equals(LocalBufferPathKey, StringComparison.OrdinalIgnoreCase))
				{
					if (keySeen)
						throw new ArgumentException($"Connection string contains '{LocalBufferPathKey}' more than once. Provide it at most once.", nameof(connectionString));
					if (string.IsNullOrWhiteSpace(value))
						throw new ArgumentException($"Connection string key '{LocalBufferPathKey}' is present but has no value. Either remove the key (no buffer) or set it to a writable absolute path.", nameof(connectionString));

					localBufferPath = value;
					keySeen = true;
					continue;
				}

				kept.Add(segment);
			}

			var sanitized = new StringBuilder();
			for (int i = 0; i < kept.Count; i++)
			{
				if (i > 0) sanitized.Append(';');
				sanitized.Append(kept[i]);
			}

			return (sanitized.ToString(), localBufferPath);
		}
	}
}
