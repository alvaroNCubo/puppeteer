using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class MaterializationCheckpointStorageMySQL : MaterializationCheckpointStorage
	{
		internal MaterializationCheckpointStorageMySQL(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			CreateTablesIfNotExist();
		}

		private void CreateTablesIfNotExist()
		{
			string sql = @"
				CREATE TABLE IF NOT EXISTS MaterializationCheckpoint
				(
					Destination VARCHAR(255) NOT NULL,
					RegisteredAtEntryId BIGINT NOT NULL,
					LastConfirmedEntryId BIGINT NOT NULL,
					RegisteredAt DATETIME(3) NOT NULL,
					ConfirmedAt DATETIME(3) NOT NULL,
					PRIMARY KEY (Destination)
				) ENGINE=InnoDB CHARSET=utf8;
			";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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
				INSERT IGNORE INTO MaterializationCheckpoint
					(Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt)
				VALUES
					(@Destination, @RegisteredAtEntryId, @LastConfirmedEntryId, @RegisteredAt, @ConfirmedAt);";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@RegisteredAtEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@LastConfirmedEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@RegisteredAt", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
						command.Parameters.AddWithValue("@ConfirmedAt", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
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

		protected internal override async Task<bool> RegisterAsync(string destination, long registeredAtEntryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");

			string sql = @"
				INSERT IGNORE INTO MaterializationCheckpoint
					(Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt)
				VALUES
					(@Destination, @RegisteredAtEntryId, @LastConfirmedEntryId, @RegisteredAt, @ConfirmedAt);";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@RegisteredAtEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@LastConfirmedEntryId", registeredAtEntryId);
						command.Parameters.AddWithValue("@RegisteredAt", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
						command.Parameters.AddWithValue("@ConfirmedAt", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
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

		protected internal override bool Deregister(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "DELETE FROM MaterializationCheckpoint WHERE Destination = @Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

			string sql = "SELECT LastConfirmedEntryId FROM MaterializationCheckpoint WHERE Destination = @Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

			string sql = "SELECT LastConfirmedEntryId FROM MaterializationCheckpoint WHERE Destination = @Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

			// UPDATE solo si avanza (Max-monotonic, idempotente). El WHERE garantiza
			// que retries con el mismo entryId no afecten la row.
			string sql = @"
				UPDATE MaterializationCheckpoint
				SET LastConfirmedEntryId = @EntryId, ConfirmedAt = @Now
				WHERE Destination = @Destination AND LastConfirmedEntryId < @EntryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@EntryId", entryId);
						command.Parameters.AddWithValue("@Now", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
						int affected = command.ExecuteNonQuery();

						if (affected > 0) return true;

						// Affected = 0 → o no avanzo, o la destination no existe.
						// Distinguir con un SELECT separado para lanzar el error correcto.
						using (MySqlCommand check = new MySqlCommand(
							"SELECT COUNT(*) FROM MaterializationCheckpoint WHERE Destination = @Destination", connection))
						{
							check.Parameters.AddWithValue("@Destination", destination);
							long count = Convert.ToInt64(check.ExecuteScalar());
							if (count == 0)
							{
								throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ConfirmUntil.");
							}
						}
						return false;
					}
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
				WHERE Destination = @Destination AND LastConfirmedEntryId < @EntryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						command.Parameters.AddWithValue("@EntryId", entryId);
						command.Parameters.AddWithValue("@Now", now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
						int affected = await command.ExecuteNonQueryAsync();

						if (affected > 0) return true;

						using (MySqlCommand check = new MySqlCommand(
							"SELECT COUNT(*) FROM MaterializationCheckpoint WHERE Destination = @Destination", connection))
						{
							check.Parameters.AddWithValue("@Destination", destination);
							long count = Convert.ToInt64(await check.ExecuteScalarAsync());
							if (count == 0)
							{
								throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ConfirmUntil.");
							}
						}
						return false;
					}
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
				FROM MaterializationCheckpoint
				ORDER BY Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						using (MySqlDataReader reader = command.ExecuteReader())
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
				FROM MaterializationCheckpoint
				ORDER BY Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						using (MySqlDataReader reader = (MySqlDataReader)await command.ExecuteReaderAsync())
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
