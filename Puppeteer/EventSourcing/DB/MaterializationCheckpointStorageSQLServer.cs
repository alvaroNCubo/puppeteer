using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class MaterializationCheckpointStorageSQLServer : MaterializationCheckpointStorage
	{
		internal MaterializationCheckpointStorageSQLServer(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			CreateTablesIfNotExist();
		}

		private void CreateTablesIfNotExist()
		{
			string sql = @"
				IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'MaterializationCheckpoint')
				BEGIN
					CREATE TABLE MaterializationCheckpoint
					(
						Destination NVARCHAR(255) NOT NULL,
						RegisteredAtEntryId BIGINT NOT NULL,
						LastConfirmedEntryId BIGINT NOT NULL,
						RegisteredAt DATETIME NOT NULL,
						ConfirmedAt DATETIME NOT NULL,
						CONSTRAINT PK_MaterializationCheckpoint PRIMARY KEY (Destination)
					);
				END
			";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.ExecuteNonQuery();
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override bool Register(string destination, long registeredAtEntryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");

			string sql = @"
				IF NOT EXISTS (SELECT 1 FROM MaterializationCheckpoint WHERE Destination = @Destination)
				BEGIN
					INSERT INTO MaterializationCheckpoint
						(Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt)
					VALUES
						(@Destination, @RegisteredAtEntryId, @LastConfirmedEntryId, @RegisteredAt, @ConfirmedAt);
					SELECT 1;
				END
				ELSE
				BEGIN
					SELECT 0;
				END";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@RegisteredAtEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@LastConfirmedEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@RegisteredAt", now);
						command.Parameters.AddWithValue("@ConfirmedAt", now);
						int result = (int)command.ExecuteScalar();
						return result == 1;
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task<bool> RegisterAsync(string destination, long registeredAtEntryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");

			string sql = @"
				IF NOT EXISTS (SELECT 1 FROM MaterializationCheckpoint WHERE Destination = @Destination)
				BEGIN
					INSERT INTO MaterializationCheckpoint
						(Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt)
					VALUES
						(@Destination, @RegisteredAtEntryId, @LastConfirmedEntryId, @RegisteredAt, @ConfirmedAt);
					SELECT 1;
				END
				ELSE
				BEGIN
					SELECT 0;
				END";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@RegisteredAtEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@LastConfirmedEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@RegisteredAt", now);
						command.Parameters.AddWithValue("@ConfirmedAt", now);
						int result = (int)await command.ExecuteScalarAsync();
						return result == 1;
					}
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}

		protected internal override bool Deregister(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "DELETE FROM MaterializationCheckpoint WHERE Destination = @Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						int affected = command.ExecuteNonQuery();
						return affected > 0;
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task<bool> DeregisterAsync(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "DELETE FROM MaterializationCheckpoint WHERE Destination = @Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						int affected = await command.ExecuteNonQueryAsync();
						return affected > 0;
					}
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}

		protected internal override bool TryGetWatermark(string destination, out long lastConfirmedEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "SELECT LastConfirmedEntryId FROM MaterializationCheckpoint WITH (NOLOCK) WHERE Destination = @Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						object result = command.ExecuteScalar();
						if (result == null || result == DBNull.Value)
						{
							lastConfirmedEntryId = 0;
							return false;
						}
						lastConfirmedEntryId = Convert.ToInt64(result);
						return true;
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task<(bool found, long lastConfirmedEntryId)> TryGetWatermarkAsync(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "SELECT LastConfirmedEntryId FROM MaterializationCheckpoint WITH (NOLOCK) WHERE Destination = @Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						object result = await command.ExecuteScalarAsync();
						if (result == null || result == DBNull.Value)
						{
							return (false, 0);
						}
						return (true, Convert.ToInt64(result));
					}
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}

		protected internal override bool ConfirmUntil(string destination, long entryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");

			// UPDATE only when it advances (Max-monotonic, idempotent).
			string sql = @"
				UPDATE MaterializationCheckpoint
				SET LastConfirmedEntryId = @EntryId, ConfirmedAt = @Now
				WHERE Destination = @Destination AND LastConfirmedEntryId < @EntryId;
				SELECT @@ROWCOUNT;";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					int affected;
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@EntryId", entryId);
						command.Parameters.AddWithValue("@Now", now);
						affected = (int)command.ExecuteScalar();
					}

					if (affected > 0) return true;

					// Affected = 0 → either it did not advance, or the destination does not exist.
					using (SqlCommand check = new SqlCommand(
						"SELECT COUNT(*) FROM MaterializationCheckpoint WITH (NOLOCK) WHERE Destination = @Destination", connection))
					{
						check.Parameters.AddWithValue("@Destination", destination);
						int count = (int)check.ExecuteScalar();
						if (count == 0)
						{
							throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ConfirmUntil.");
						}
					}
					return false;
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task<bool> ConfirmUntilAsync(string destination, long entryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");

			string sql = @"
				UPDATE MaterializationCheckpoint
				SET LastConfirmedEntryId = @EntryId, ConfirmedAt = @Now
				WHERE Destination = @Destination AND LastConfirmedEntryId < @EntryId;
				SELECT @@ROWCOUNT;";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					int affected;
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@EntryId", entryId);
						command.Parameters.AddWithValue("@Now", now);
						affected = (int)await command.ExecuteScalarAsync();
					}

					if (affected > 0) return true;

					using (SqlCommand check = new SqlCommand(
						"SELECT COUNT(*) FROM MaterializationCheckpoint WITH (NOLOCK) WHERE Destination = @Destination", connection))
					{
						check.Parameters.AddWithValue("@Destination", destination);
						int count = (int)await check.ExecuteScalarAsync();
						if (count == 0)
						{
							throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ConfirmUntil.");
						}
					}
					return false;
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}

		protected internal override void List(List<MaterializationCheckpointRow> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = @"
				SELECT Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt
				FROM MaterializationCheckpoint WITH (NOLOCK)
				ORDER BY Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						using (SqlDataReader reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								result.Add(new MaterializationCheckpointRow(
									reader.GetString(0),
									reader.GetInt64(1),
									reader.GetInt64(2),
									reader.GetDateTime(3),
									reader.GetDateTime(4)));
							}
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task ListAsync(List<MaterializationCheckpointRow> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = @"
				SELECT Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt
				FROM MaterializationCheckpoint WITH (NOLOCK)
				ORDER BY Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						using (SqlDataReader reader = await command.ExecuteReaderAsync())
						{
							while (await reader.ReadAsync())
							{
								result.Add(new MaterializationCheckpointRow(
									reader.GetString(0),
									reader.GetInt64(1),
									reader.GetInt64(2),
									reader.GetDateTime(3),
									reader.GetDateTime(4)));
							}
						}
					}
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}
	}
}
