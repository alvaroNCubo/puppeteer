using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class EventElisionStorageMySQL : EventElisionStorage
	{
		internal EventElisionStorageMySQL(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			CreateTablesIfNotExist();
		}

		private void CreateTablesIfNotExist()
		{
			string sql = @"
				CREATE TABLE IF NOT EXISTS EventElision
				(
					DiaryId BIGINT NOT NULL,
					ReactionId INT NOT NULL,
					Timestamp DATETIME(3) NOT NULL,
					INDEX IX_EventElision_DairyId (DiaryId)
				) ENGINE=InnoDB CHARSET=utf8;

				CREATE TABLE IF NOT EXISTS EventElisionBuffer
				(
					DiaryId BIGINT NOT NULL,
					ReactionId INT NOT NULL,
					Timestamp DATETIME(3) NOT NULL,
					INDEX IX_EventElisionBuffer_DairyId (DiaryId)
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

		protected internal override bool IsEventElided(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			string sql = "SELECT COUNT(*) FROM EventElision WHERE DiaryId = @DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
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

		protected internal override async Task<bool> IsEventElidedAsync(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			string sql = "SELECT COUNT(*) FROM EventElision WHERE DiaryId = @DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@DiaryId", dairyId);
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

		protected internal override void MarkEventsAsElided(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			if (dairyIds.Length == 0) return;

			string journalTable = EventJournalClient.ActorName;

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				connection.Open();
				using (MySqlTransaction transaction = connection.BeginTransaction())
				{
					try
					{
						// 0. Limpiar buffer (los rollback dejarian residuo).
						using (MySqlCommand clearCmd = new MySqlCommand("DELETE FROM EventElisionBuffer", connection, transaction))
						{
							clearCmd.ExecuteNonQuery();
						}

						// 1. Insertar en buffer con batched VALUES.
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

							using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection, transaction))
							{
								command.Parameters.AddWithValue("@ReactionId", reactionId);
								command.Parameters.AddWithValue("@Timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));

								for (int i = 0; i < batchCount; i++)
								{
									command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
								}

								command.ExecuteNonQuery();
							}

							sql.Clear();
						}

						// 2. Commit logico bajo la misma transaccion DB:
						//    (a) copiar buffer → EventElision (registry completo con reactionId/timestamp).
						//    (b) UPDATE journal SET Skip = 1 para los DiaryIds del buffer — column
						//        autoritativa para rehidratacion (sin LEFT JOIN). Materialize v2 /
						//        Fase 0.5: Skip column es el dato; rehidratacion lee Skip = 0 sin join.
						string commitSql = $@"
							INSERT INTO EventElision (DiaryId, ReactionId, Timestamp)
							SELECT DiaryId, ReactionId, Timestamp FROM EventElisionBuffer;
							UPDATE `{journalTable}` SET Skip = 1 WHERE id IN (SELECT DiaryId FROM EventElisionBuffer);
						";

						using (MySqlCommand commitCmd = new MySqlCommand(commitSql, connection, transaction))
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

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				await connection.OpenAsync();
				using (MySqlTransaction transaction = await connection.BeginTransactionAsync())
				{
					try
					{
						using (MySqlCommand clearCmd = new MySqlCommand("DELETE FROM EventElisionBuffer", connection, transaction))
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

							using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection, transaction))
							{
								command.Parameters.AddWithValue("@ReactionId", reactionId);
								command.Parameters.AddWithValue("@Timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));

								for (int i = 0; i < batchCount; i++)
								{
									command.Parameters.AddWithValue($"@DiaryId{i}", dairyIds[startIdx + i]);
								}

								await command.ExecuteNonQueryAsync();
							}
							sql.Clear();
						}

						string commitSql = $@"
							INSERT INTO EventElision (DiaryId, ReactionId, Timestamp)
							SELECT DiaryId, ReactionId, Timestamp FROM EventElisionBuffer;
							UPDATE `{journalTable}` SET Skip = 1 WHERE id IN (SELECT DiaryId FROM EventElisionBuffer);
						";

						using (MySqlCommand commitCmd = new MySqlCommand(commitSql, connection, transaction))
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

			string sql = "SELECT DiaryId FROM EventElision WHERE ReactionId = @ReactionId ORDER BY DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
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

		protected internal override async Task GetElidedEventsByReactionAsync(int reactionId, List<long> result)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			string sql = "SELECT DiaryId FROM EventElision WHERE ReactionId = @ReactionId ORDER BY DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
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

		protected internal override void GetElidedEventsInRange(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId
				FROM EventElision
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
			";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
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

		protected internal override async Task GetElidedEventsInRangeAsync(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId
				FROM EventElision
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
			";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
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

		// Materialize v2 / Fase 3 — wire verb (d) DameElidedRange. Ordenado por
		// (Timestamp, DiaryId) desde EventElision.Timestamp (sin tabla nueva, sin
		// MarkingOrder autoincrement — firmado por Alvaro 2026-05-13 PM).
		protected internal override void ReadElisionMarkersInRange(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			string sql = @"
				SELECT DiaryId, ReactionId, Timestamp
				FROM EventElision
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
				ORDER BY Timestamp, DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
						using (MySqlDataReader reader = command.ExecuteReader())
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
				FROM EventElision
				WHERE DiaryId >= @FromDairyId AND DiaryId <= @ToDairyId
				ORDER BY Timestamp, DiaryId";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@FromDairyId", fromDairyId);
						command.Parameters.AddWithValue("@ToDairyId", toDairyId);
						using (MySqlDataReader reader = (MySqlDataReader)await command.ExecuteReaderAsync())
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
