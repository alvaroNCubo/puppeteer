using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class EventElisionStorageSQLServer : EventElisionStorage
	{
		internal EventElisionStorageSQLServer(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			CreateTablesIfNotExist();
		}

		private void CreateTablesIfNotExist()
		{
			string sql = @"
				IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventElision')
				BEGIN
					CREATE TABLE EventElision
					(
						DiaryId BIGINT NOT NULL,
						ReactionId INT NOT NULL,
						Timestamp DATETIME NOT NULL
					);

					CREATE NONCLUSTERED INDEX IX_EventElision_DairyId
					ON EventElision (DiaryId);
				END

				IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'EventElisionBuffer')
				BEGIN
					CREATE TABLE EventElisionBuffer
					(
						DiaryId BIGINT NOT NULL,
						ReactionId INT NOT NULL,
						Timestamp DATETIME NOT NULL
					);

					CREATE NONCLUSTERED INDEX IX_EventElisionBuffer_DairyId
					ON EventElisionBuffer (DiaryId);
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

		protected internal override bool IsEventElided(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			string sql = "SELECT COUNT(*) FROM EventElision WITH (NOLOCK) WHERE DiaryId = @DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
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

		protected internal override async Task<bool> IsEventElidedAsync(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			string sql = "SELECT COUNT(*) FROM EventElision WITH (NOLOCK) WHERE DiaryId = @DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
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

		protected internal override void MarkEventsAsElided(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			if (dairyIds.Length == 0) return;

			string journalTable = EventJournalClient.ActorName;

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				connection.Open();
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					try
					{
						using (SqlCommand clearCmd = new SqlCommand("DELETE FROM EventElisionBuffer", connection, transaction))
						{
							clearCmd.ExecuteNonQuery();
						}

						const int BATCH_SIZE = 1000;
						int totalBatches = (dairyIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

						StringBuilder sql = new StringBuilder();
						for (int batch = 0; batch < totalBatches; batch++)
						{
							int startIdx = batch * BATCH_SIZE;
							int endIdx = Math.Min(startIdx + BATCH_SIZE, dairyIds.Length);
							int batchCount = endIdx - startIdx;

							sql.Append("INSERT INTO EventElisionBuffer (DiaryId, ReactionId, Timestamp) VALUES ");

							for (int i = 0; i < batchCount; i++)
							{
								if (i > 0) sql.Append(", ");
								sql.Append($"(@DiaryId{i}, @ReactionId, @Timestamp)");
							}

							using (SqlCommand command = new SqlCommand(sql.ToString(), connection, transaction))
							{
								command.Parameters.AddWithValue("@ReactionId", reactionId);
								command.Parameters.AddWithValue("@Timestamp", timestamp);

								for (int i = 0; i < batchCount; i++)
								{
									command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
								}

								command.ExecuteNonQuery();
							}

							sql.Clear();
						}

						// Logical commit under the same DB transaction:
						// (a) MERGE buffer into EventElision (full registry).
						// (b) UPDATE journal SET Skip = 1 — authoritative column for rehydration
						//     (no LEFT JOIN). Materialize v2 / Phase 0.5.
						string commitSql = $@"
							MERGE INTO EventElision AS target
							USING EventElisionBuffer AS source
							ON target.DiaryId = source.DiaryId AND target.ReactionId = source.ReactionId
							WHEN MATCHED THEN
								UPDATE SET Timestamp = source.Timestamp
							WHEN NOT MATCHED THEN
								INSERT (DiaryId, ReactionId, Timestamp)
								VALUES (source.DiaryId, source.ReactionId, source.Timestamp);

							UPDATE j SET j.[Skip] = 1
							FROM [{journalTable}] j
							INNER JOIN EventElisionBuffer b ON j.id = b.DiaryId;
						";

						using (SqlCommand commitCmd = new SqlCommand(commitSql, connection, transaction))
						{
							commitCmd.ExecuteNonQuery();
						}

						transaction.Commit();
					}
					catch
					{
						transaction.Rollback();
						throw;
					}
				}
			}
		}

		protected internal override async Task MarkEventsAsElidedAsync(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			if (dairyIds.Length == 0) return;

			string journalTable = EventJournalClient.ActorName;

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				await connection.OpenAsync();
				using (SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync())
				{
					try
					{
						using (SqlCommand clearCmd = new SqlCommand("DELETE FROM EventElisionBuffer", connection, transaction))
						{
							await clearCmd.ExecuteNonQueryAsync();
						}

						const int BATCH_SIZE = 1000;
						int totalBatches = (dairyIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

						StringBuilder sql = new StringBuilder();
						for (int batch = 0; batch < totalBatches; batch++)
						{
							int startIdx = batch * BATCH_SIZE;
							int endIdx = Math.Min(startIdx + BATCH_SIZE, dairyIds.Length);
							int batchCount = endIdx - startIdx;

							sql.Append("INSERT INTO EventElisionBuffer (DiaryId, ReactionId, Timestamp) VALUES ");

							for (int i = 0; i < batchCount; i++)
							{
								if (i > 0) sql.Append(", ");
								sql.Append($"(@DiaryId{i}, @ReactionId, @Timestamp)");
							}

							using (SqlCommand command = new SqlCommand(sql.ToString(), connection, transaction))
							{
								command.Parameters.AddWithValue("@ReactionId", reactionId);
								command.Parameters.AddWithValue("@Timestamp", timestamp);

								for (int i = 0; i < batchCount; i++)
								{
									command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
								}

								await command.ExecuteNonQueryAsync();
							}

							sql.Clear();
						}

						string commitSql = $@"
							MERGE INTO EventElision AS target
							USING EventElisionBuffer AS source
							ON target.DiaryId = source.DiaryId AND target.ReactionId = source.ReactionId
							WHEN MATCHED THEN
								UPDATE SET Timestamp = source.Timestamp
							WHEN NOT MATCHED THEN
								INSERT (DiaryId, ReactionId, Timestamp)
								VALUES (source.DiaryId, source.ReactionId, source.Timestamp);

							UPDATE j SET j.[Skip] = 1
							FROM [{journalTable}] j
							INNER JOIN EventElisionBuffer b ON j.id = b.DiaryId;
						";

						using (SqlCommand commitCmd = new SqlCommand(commitSql, connection, transaction))
						{
							await commitCmd.ExecuteNonQueryAsync();
						}

						await transaction.CommitAsync();
					}
					catch
					{
						await transaction.RollbackAsync();
						throw;
					}
				}
			}
		}

		protected internal override void GetElidedEventsByReaction(int reactionId, List<long> result)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = "SELECT DiaryId FROM EventElision WITH (NOLOCK) WHERE ReactionId = @ReactionId ORDER BY DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
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

		protected internal override async Task GetElidedEventsByReactionAsync(int reactionId, List<long> result)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = "SELECT DiaryId FROM EventElision WITH (NOLOCK) WHERE ReactionId = @ReactionId ORDER BY DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
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

		protected internal override void GetElidedEventsInRange(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId
				FROM EventElision WITH (NOLOCK)
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
			";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
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

		protected internal override async Task GetElidedEventsInRangeAsync(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId
				FROM EventElision WITH (NOLOCK)
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
			";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
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

		// Materialize v2 / Phase 3 — wire verb (d) DameElidedRange. Ordered by
		// (Timestamp, DiaryId) from EventElision.Timestamp.
		protected internal override void ReadElisionMarkersInRange(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId, ReactionId, Timestamp
				FROM EventElision WITH (NOLOCK)
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
				ORDER BY Timestamp, DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
						using (SqlDataReader reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								result.Add(new MaterializationElisionMarker(
									reader.GetInt64(0),
									reader.GetInt32(1),
									reader.GetDateTime(2)));
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

		protected internal override async Task ReadElisionMarkersInRangeAsync(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId, ReactionId, Timestamp
				FROM EventElision WITH (NOLOCK)
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
				ORDER BY Timestamp, DiaryId";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
						using (SqlDataReader reader = await command.ExecuteReaderAsync())
						{
							while (await reader.ReadAsync())
							{
								result.Add(new MaterializationElisionMarker(
									reader.GetInt64(0),
									reader.GetInt32(1),
									reader.GetDateTime(2)));
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
