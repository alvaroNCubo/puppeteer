using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace Puppeteer.EventSourcing.Playbill
{
	// MySQL backend of the Playbill. Lives in the same DB as the actor's journal
	// (one-actor-per-database). Auto-provision: the constructor creates the two
	// tables (PlaybillSchemas, PlaybillRecords) via CREATE TABLE IF NOT EXISTS.
	//
	// Distill via rebuild-via-shadow-swap: filters out records that point to
	// EntryIds no longer existing in the `{ActorName}` journal and atomically
	// replaces the table. The JOIN query against the actor's table materializes
	// the "journal query" described in PlaybillStore — it does not mix with the
	// Diary's strategy; each storage Distills its own.
	internal sealed class PlaybillStoreMySQL : PlaybillStore
	{
		private const string SCHEMAS_TABLE = "PlaybillSchemas";
		private const string RECORDS_TABLE = "PlaybillRecords";

		internal PlaybillStoreMySQL(string actorName, string connectionString, IPuppeteerLogger logger)
			: base(actorName, connectionString, logger)
		{
			EnsureTables();
		}

		private void EnsureTables()
		{
			string sql = $@"
				CREATE TABLE IF NOT EXISTS `{SCHEMAS_TABLE}` (
					SchemaName    VARCHAR(64)   NOT NULL,
					Declarations  VARCHAR(2000) NOT NULL,
					CreatedAt     DATETIME      NOT NULL,
					PRIMARY KEY (SchemaName)
				) ENGINE=InnoDB CHARSET=utf8mb4;

				CREATE TABLE IF NOT EXISTS `{RECORDS_TABLE}` (
					EntryId              BIGINT        NOT NULL,
					SchemaName           VARCHAR(64)   NOT NULL,
					SerializedParameters VARCHAR(2000) NOT NULL,
					PRIMARY KEY (EntryId),
					INDEX IX_PlaybillRecords_SchemaName (SchemaName)
				) ENGINE=InnoDB CHARSET=utf8mb4;";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new MySqlCommand(sql, connection))
					{
						command.ExecuteNonQuery();
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($"EnsureTables Playbill on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to provision Playbill tables on MySQL for actor '{ActorName}': {e.Message}");
				}
				finally
				{
					connection.Close();
				}
			}
		}

		internal override void RegisterSchema(string schemaName, string declarations)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(declarations);

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					string existing = SelectDeclarations(connection, schemaName);
					if (existing != null)
					{
						if (existing != declarations)
						{
							throw new LanguageException(
								$"Playbill schema '{schemaName}' is already registered with a different shape. " +
								$"Existing: '{existing}'. New: '{declarations}'. Schema drift requires migration.");
						}
						// Idempotent re-register — fire callback (Cast receivers are idempotent too,
						// see PlaybillStoreInMemory comment in the abstract base) and return.
						OnSchemaRegistered?.Invoke(schemaName, declarations);
						return;
					}

					string sql = $"INSERT INTO `{SCHEMAS_TABLE}` (SchemaName, Declarations, CreatedAt) VALUES (@SchemaName, @Declarations, @CreatedAt)";
					using (var command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@SchemaName", schemaName);
						command.Parameters.AddWithValue("@Declarations", declarations);
						command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);
						command.ExecuteNonQuery();
					}
				}
				catch (LanguageException)
				{
					throw;
				}
				catch (MySqlException e)
				{
					Logger.Error($"RegisterSchema '{schemaName}' on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to register Playbill schema '{schemaName}' on MySQL: {e.Message}");
				}
				finally
				{
					connection.Close();
				}
			}

			// Fire callback OUTSIDE the connection block: subscribers (e.g. Stage broadcasting
			// a PlaybillSchemaCue) should not be inside the DB transaction. If a subscriber
			// throws, the INSERT is already committed — the exception propagates to the caller
			// but the schema is persisted, matching the let-it-fail policy for the second write.
			OnSchemaRegistered?.Invoke(schemaName, declarations);
		}

		private string SelectDeclarations(MySqlConnection connection, string schemaName)
		{
			string sql = $"SELECT Declarations FROM `{SCHEMAS_TABLE}` WHERE SchemaName = @SchemaName";
			using (var command = new MySqlCommand(sql, connection))
			{
				command.Parameters.AddWithValue("@SchemaName", schemaName);
				using (var reader = command.ExecuteReader())
				{
					if (reader.Read())
					{
						return reader.GetString(0);
					}
				}
			}
			return null;
		}

		internal override string GetSchemaDeclarations(string schemaName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					return SelectDeclarations(connection, schemaName);
				}
				finally
				{
					connection.Close();
				}
			}
		}

		internal override IEnumerable<(string Name, string Declarations)> ListSchemas()
		{
			var result = new List<(string, string)>();
			string sql = $"SELECT SchemaName, Declarations FROM `{SCHEMAS_TABLE}` ORDER BY SchemaName";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new MySqlCommand(sql, connection))
					using (var reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							result.Add((reader.GetString(0), reader.GetString(1)));
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}

			return result;
		}

		internal override void WriteRecord(long entryId, string schemaName, string serializedParameters)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(serializedParameters);

			string sql = $"INSERT INTO `{RECORDS_TABLE}` (EntryId, SchemaName, SerializedParameters) VALUES (@EntryId, @SchemaName, @SerializedParameters)";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@EntryId", entryId);
						command.Parameters.AddWithValue("@SchemaName", schemaName);
						command.Parameters.AddWithValue("@SerializedParameters", serializedParameters);
						command.ExecuteNonQuery();
					}
				}
				catch (MySqlException e)
				{
					// Duplicate key (PK violation) — second write of the same EntryId.
					if (e.Number == 1062)
					{
						throw new LanguageException($"Playbill record for EntryId {entryId} already exists (expected at most one per entry).");
					}
					Logger.Error($"WriteRecord entryId={entryId} schema='{schemaName}' on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to write Playbill record on MySQL for EntryId {entryId}: {e.Message}");
				}
				finally
				{
					connection.Close();
				}
			}

			// Fire callback OUTSIDE the connection block (see RegisterSchema for rationale).
			OnRecordWritten?.Invoke(entryId, schemaName, serializedParameters);
		}

		internal override (string SchemaName, string SerializedParameters)? ReadRecord(long entryId)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");

			string sql = $"SELECT SchemaName, SerializedParameters FROM `{RECORDS_TABLE}` WHERE EntryId = @EntryId";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@EntryId", entryId);
						using (var reader = command.ExecuteReader())
						{
							if (reader.Read())
							{
								return (reader.GetString(0), reader.GetString(1));
							}
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}

			return null;
		}

		internal override IEnumerable<(long EntryId, string SerializedParameters)> ReadRecordsForSchema(string schemaName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);

			var result = new List<(long, string)>();
			string sql = $"SELECT EntryId, SerializedParameters FROM `{RECORDS_TABLE}` WHERE SchemaName = @SchemaName ORDER BY EntryId ASC";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@SchemaName", schemaName);
						using (var reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								result.Add((reader.GetInt64(0), reader.GetString(1)));
							}
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}

			return result;
		}

		internal override void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result)
		{
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();
			string sql = $"SELECT EntryId, SchemaName, SerializedParameters FROM `{RECORDS_TABLE}` WHERE EntryId > @AfterEntryId ORDER BY EntryId ASC";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@AfterEntryId", afterEntryId);
						using (var reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								long entryId = reader.GetInt64(0);
								string schemaName = reader.GetString(1);
								string serialized = reader.GetString(2);
								result.Add(new PlaybillRecord(entryId, schemaName, serialized));
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

		// Rebuild-via-shadow-swap: clones the table with the full structure
		// (including indexes) via CREATE TABLE LIKE, inserts only records whose
		// EntryId is still alive in the actor's journal, and replaces atomically.
		// A multi-step RENAME TABLE in MySQL is atomic — it needs no explicit
		// transaction; no client sees an intermediate state.
		internal override void Distill()
		{
			string newTable = RECORDS_TABLE + "_new";
			string oldTable = RECORDS_TABLE + "_old";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					// Defensive cleanup in case a previous Distill aborted mid-way
					// and left shadow tables behind.
					ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS `{newTable}`");
					ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS `{oldTable}`");

					ExecuteNonQuery(connection, $"CREATE TABLE `{newTable}` LIKE `{RECORDS_TABLE}`");

					string copyAlive = $@"
						INSERT INTO `{newTable}`
						SELECT pr.* FROM `{RECORDS_TABLE}` pr
						WHERE EXISTS (SELECT 1 FROM `{ActorName}` j WHERE j.id = pr.EntryId)";
					ExecuteNonQuery(connection, copyAlive);

					ExecuteNonQuery(connection, $"RENAME TABLE `{RECORDS_TABLE}` TO `{oldTable}`, `{newTable}` TO `{RECORDS_TABLE}`");
					ExecuteNonQuery(connection, $"DROP TABLE `{oldTable}`");
				}
				catch (MySqlException e)
				{
					Logger.Error($"Distill Playbill on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to distill Playbill on MySQL for actor '{ActorName}': {e.Message}");
				}
				finally
				{
					connection.Close();
				}
			}
		}

		private static void ExecuteNonQuery(MySqlConnection connection, string sql)
		{
			using (var command = new MySqlCommand(sql, connection))
			{
				command.CommandType = CommandType.Text;
				command.ExecuteNonQuery();
			}
		}

		internal override MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			throw new NotImplementedException("Archive pending for PlaybillStoreMySQL");
		}
	}
}
