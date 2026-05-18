using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class EventMaterializationStorageSQLServer : EventMaterializationStorage
	{
		internal EventMaterializationStorageSQLServer(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			CreateTablesIfNotExist();
		}

		private void CreateTablesIfNotExist()
		{
			string sql = @"
				IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventMaterialization')
				BEGIN
					CREATE TABLE EventMaterialization
					(
						DiaryId BIGINT NOT NULL,
						ReactionId INT NOT NULL,
						Destination NVARCHAR(255) NOT NULL,
						Timestamp DATETIME NOT NULL,
						CONSTRAINT PK_EventMaterialization PRIMARY KEY (DiaryId, Destination)
					);

					CREATE NONCLUSTERED INDEX IX_EventMaterialization_Destination
					ON EventMaterialization (Destination);
				END

				IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventMaterializationBuffer')
				BEGIN
					CREATE TABLE EventMaterializationBuffer
					(
						DiaryId BIGINT NOT NULL,
						ReactionId INT NOT NULL,
						Destination NVARCHAR(255) NOT NULL,
						Timestamp DATETIME NOT NULL
					);

					CREATE NONCLUSTERED INDEX IX_EventMaterializationBuffer_Destination
					ON EventMaterializationBuffer (Destination);
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

		protected internal override bool IsEventMaterialized(long dairyId, string destination)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			string sql = "SELECT COUNT(*) FROM EventMaterialization WITH (NOLOCK) WHERE DiaryId = @DiaryId AND Destination = @Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
						command.Parameters.AddWithValue("@Destination", destination);
						int count = (int)command.ExecuteScalar();
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

			string sql = "SELECT COUNT(*) FROM EventMaterialization WITH (NOLOCK) WHERE DiaryId = @DiaryId AND Destination = @Destination";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
						command.Parameters.AddWithValue("@Destination", destination);
						int count = (int)await command.ExecuteScalarAsync();
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

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					StringBuilder sql = new StringBuilder();
					sql.Append("DELETE FROM EventMaterializationBuffer; ");

					const int BATCH_SIZE = 1000;
					int totalBatches = (dairyIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

					connection.Open();

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

						using (SqlCommand command = new SqlCommand(sql.ToString(), connection))
						{
							command.Parameters.AddWithValue("@ReactionId", reactionId);
							command.Parameters.AddWithValue("@Destination", destination);
							command.Parameters.AddWithValue("@Timestamp", timestamp);

							for (int i = 0; i < batchCount; i++)
							{
								command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
							}

							command.ExecuteNonQuery();
						}

						sql.Clear();
					}

					string commitSql = @"
						MERGE INTO EventMaterialization AS target
						USING EventMaterializationBuffer AS source
						ON target.DiaryId = source.DiaryId AND target.Destination = source.Destination
						WHEN MATCHED THEN
							UPDATE SET Timestamp = source.Timestamp, ReactionId = source.ReactionId
						WHEN NOT MATCHED THEN
							INSERT (DiaryId, ReactionId, Destination, Timestamp)
							VALUES (source.DiaryId, source.ReactionId, source.Destination, source.Timestamp);
					";

					using (SqlCommand commitCmd = new SqlCommand(commitSql, connection))
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

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					StringBuilder sql = new StringBuilder();
					sql.Append("DELETE FROM EventMaterializationBuffer; ");

					const int BATCH_SIZE = 1000;
					int totalBatches = (dairyIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

					await connection.OpenAsync();

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

						using (SqlCommand command = new SqlCommand(sql.ToString(), connection))
						{
							command.Parameters.AddWithValue("@ReactionId", reactionId);
							command.Parameters.AddWithValue("@Destination", destination);
							command.Parameters.AddWithValue("@Timestamp", timestamp);

							for (int i = 0; i < batchCount; i++)
							{
								command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
							}

							await command.ExecuteNonQueryAsync();
						}

						sql.Clear();
					}

					string commitSql = @"
						MERGE INTO EventMaterialization AS target
						USING EventMaterializationBuffer AS source
						ON target.DiaryId = source.DiaryId AND target.Destination = source.Destination
						WHEN MATCHED THEN
							UPDATE SET Timestamp = source.Timestamp, ReactionId = source.ReactionId
						WHEN NOT MATCHED THEN
							INSERT (DiaryId, ReactionId, Destination, Timestamp)
							VALUES (source.DiaryId, source.ReactionId, source.Destination, source.Timestamp);
					";

					using (SqlCommand commitCmd = new SqlCommand(commitSql, connection))
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

			string sql = "SELECT DiaryId FROM EventMaterialization WITH (NOLOCK) WHERE Destination = @Destination ORDER BY DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						using (SqlDataReader reader = command.ExecuteReader())
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

			string sql = "SELECT DiaryId FROM EventMaterialization WITH (NOLOCK) WHERE Destination = @Destination ORDER BY DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@Destination", destination);
						using (SqlDataReader reader = await command.ExecuteReaderAsync())
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
