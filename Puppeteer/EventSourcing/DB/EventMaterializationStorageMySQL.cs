using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class EventMaterializationStorageMySQL : EventMaterializationStorage
	{
		internal EventMaterializationStorageMySQL(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			CreateTablesIfNotExist();
		}

		private void CreateTablesIfNotExist()
		{
			string sql = @"
				CREATE TABLE IF NOT EXISTS EventMaterialization
				(
					DiaryId BIGINT NOT NULL,
					ReactionId INT NOT NULL,
					Destination VARCHAR(255) NOT NULL,
					Timestamp DATETIME(3) NOT NULL,
					PRIMARY KEY (DiaryId, Destination),
					INDEX IX_EventMaterialization_Destination (Destination)
				) ENGINE=InnoDB CHARSET=utf8;

				CREATE TABLE IF NOT EXISTS EventMaterializationBuffer
				(
					DiaryId BIGINT NOT NULL,
					ReactionId INT NOT NULL,
					Destination VARCHAR(255) NOT NULL,
					Timestamp DATETIME(3) NOT NULL,
					INDEX IX_EventMaterializationBuffer_Destination (Destination)
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

		protected internal override bool IsEventMaterialized(long dairyId, string destination)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "SELECT COUNT(*) FROM EventMaterialization WHERE DiaryId = @DiaryId AND Destination = @Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
						command.Parameters.AddWithValue("@Destination", destination);
						long count = (long)command.ExecuteScalar();
						return count > 0;
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task<bool> IsEventMaterializedAsync(long dairyId, string destination)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "SELECT COUNT(*) FROM EventMaterialization WHERE DiaryId = @DiaryId AND Destination = @Destination";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
						command.Parameters.AddWithValue("@Destination", destination);
						long count = (long)await command.ExecuteScalarAsync();
						return count > 0;
					}
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}

		protected internal override void MarkEventsAsMaterialized(long[] dairyIds, int reactionId, string destination, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (dairyIds.Length == 0) return;

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					StringBuilder sql = new StringBuilder();
					sql.Append("DELETE FROM EventMaterializationBuffer; ");

					connection.Open();

					const int BATCH_SIZE = 1000;
					int totalBatches = (dairyIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

					for (int batch = 0; batch < totalBatches; batch++)
					{
						int startIdx = batch * BATCH_SIZE;
						int endIdx = Math.Min(startIdx + BATCH_SIZE, dairyIds.Length);
						int batchCount = endIdx - startIdx;

						sql.Append("INSERT INTO EventMaterializationBuffer (DiaryId, ReactionId, Destination, Timestamp) VALUES ");

						for (int i = 0; i < batchCount; i++)
						{
							if (i > 0) sql.Append(", ");
							sql.Append($"(@DiaryId{i}, @ReactionId, @Destination, @Timestamp)");
						}

						using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection))
						{
							command.Parameters.AddWithValue("@ReactionId", reactionId);
							command.Parameters.AddWithValue("@Destination", destination);
							command.Parameters.AddWithValue("@Timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

							for (int i = 0; i < batchCount; i++)
							{
								command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
							}

							command.ExecuteNonQuery();
						}

						sql.Clear();
					}

					// 2. Commit: move buffer -> final table. ON DUPLICATE KEY makes
					// delivery idempotent — re-marking the same (DiaryId, Destination)
					// produces no duplicate rows, it only refreshes the Timestamp.
					string commitSql = @"
						INSERT INTO EventMaterialization (DiaryId, ReactionId, Destination, Timestamp)
						SELECT DiaryId, ReactionId, Destination, Timestamp
						FROM EventMaterializationBuffer
						ON DUPLICATE KEY UPDATE Timestamp = VALUES(Timestamp);
					";

					using (MySqlCommand commitCmd = new MySqlCommand(commitSql, connection))
					{
						commitCmd.ExecuteNonQuery();
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override async Task MarkEventsAsMaterializedAsync(long[] dairyIds, int reactionId, string destination, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (dairyIds.Length == 0) return;

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					StringBuilder sql = new StringBuilder();
					sql.Append("DELETE FROM EventMaterializationBuffer; ");

					await connection.OpenAsync();

					const int BATCH_SIZE = 1000;
					int totalBatches = (dairyIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

					for (int batch = 0; batch < totalBatches; batch++)
					{
						int startIdx = batch * BATCH_SIZE;
						int endIdx = Math.Min(startIdx + BATCH_SIZE, dairyIds.Length);
						int batchCount = endIdx - startIdx;

						sql.Append("INSERT INTO EventMaterializationBuffer (DiaryId, ReactionId, Destination, Timestamp) VALUES ");

						for (int i = 0; i < batchCount; i++)
						{
							if (i > 0) sql.Append(", ");
							sql.Append($"(@DiaryId{i}, @ReactionId, @Destination, @Timestamp)");
						}

						using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection))
						{
							command.Parameters.AddWithValue("@ReactionId", reactionId);
							command.Parameters.AddWithValue("@Destination", destination);
							command.Parameters.AddWithValue("@Timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

							for (int i = 0; i < batchCount; i++)
							{
								command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
							}

							await command.ExecuteNonQueryAsync();
						}

						sql.Clear();
					}

					string commitSql = @"
						INSERT INTO EventMaterialization (DiaryId, ReactionId, Destination, Timestamp)
						SELECT DiaryId, ReactionId, Destination, Timestamp
						FROM EventMaterializationBuffer
						ON DUPLICATE KEY UPDATE Timestamp = VALUES(Timestamp);
					";

					using (MySqlCommand commitCmd = new MySqlCommand(commitSql, connection))
					{
						await commitCmd.ExecuteNonQueryAsync();
					}
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
		}

		protected internal override void GetMaterializedEventsByDestination(string destination, List<long> result)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = "SELECT DiaryId FROM EventMaterialization WHERE Destination = @Destination ORDER BY DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						using (MySqlDataReader reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								result.Add(reader.GetInt64(0));
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

		protected internal override async Task GetMaterializedEventsByDestinationAsync(string destination, List<long> result)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = "SELECT DiaryId FROM EventMaterialization WHERE Destination = @Destination ORDER BY DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						using (MySqlDataReader reader = (MySqlDataReader)await command.ExecuteReaderAsync())
						{
							while (await reader.ReadAsync())
							{
								result.Add(reader.GetInt64(0));
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
