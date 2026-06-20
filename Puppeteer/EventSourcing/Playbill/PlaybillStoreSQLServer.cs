using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace Puppeteer.EventSourcing.Playbill
{
	// Backend SQL Server del Playbill. Mismo modelo de datos que el backend MySQL,
	// distintas idiomaticas SQL: brackets [Name] en vez de backticks, IF OBJECT_ID
	// en vez de CREATE TABLE IF NOT EXISTS, SELECT INTO + sp_rename para el
	// rebuild-via-shadow-swap (RENAME TABLE no existe en SQL Server).
	internal sealed class PlaybillStoreSQLServer : PlaybillStore
	{
		private const string SCHEMAS_TABLE = "PlaybillSchemas";
		private const string RECORDS_TABLE = "PlaybillRecords";

		internal PlaybillStoreSQLServer(string actorName, string connectionString, IPuppeteerLogger logger)
			: base(actorName, connectionString, logger)
		{
			EnsureTables();
		}

		private void EnsureTables()
		{
			string sql = $@"
				IF OBJECT_ID('{SCHEMAS_TABLE}') IS NULL
				BEGIN
					CREATE TABLE [{SCHEMAS_TABLE}] (
						SchemaName    VARCHAR(64)   NOT NULL,
						Declarations  VARCHAR(2000) NOT NULL,
						CreatedAt     DATETIME      NOT NULL,
						PRIMARY KEY (SchemaName)
					);
				END;

				IF OBJECT_ID('{RECORDS_TABLE}') IS NULL
				BEGIN
					CREATE TABLE [{RECORDS_TABLE}] (
						EntryId              BIGINT        NOT NULL,
						SchemaName           VARCHAR(64)   NOT NULL,
						SerializedParameters VARCHAR(2000) NOT NULL,
						PRIMARY KEY (EntryId)
					);
					CREATE INDEX IX_PlaybillRecords_SchemaName ON [{RECORDS_TABLE}] (SchemaName);
				END;";

			using (var connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new SqlCommand(sql, connection))
					{
						command.ExecuteNonQuery();
					}
				}
				catch (SqlException e)
				{
					Logger.Error($"EnsureTables Playbill on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to provision Playbill tables on SQL Server for actor '{ActorName}': {e.Message}");
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

			using (var connection = new SqlConnection(ConnectionString))
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
						// Idempotent re-register — fire callback (Cast receivers are idempotent) and return.
						OnSchemaRegistered?.Invoke(schemaName, declarations);
						return;
					}

					string sql = $"INSERT INTO [{SCHEMAS_TABLE}] (SchemaName, Declarations, CreatedAt) VALUES (@SchemaName, @Declarations, @CreatedAt)";
					using (var command = new SqlCommand(sql, connection))
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
				catch (SqlException e)
				{
					Logger.Error($"RegisterSchema '{schemaName}' on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to register Playbill schema '{schemaName}' on SQL Server: {e.Message}");
				}
				finally
				{
					connection.Close();
				}
			}

			// Fire callback OUTSIDE the connection block: subscribers (e.g. Stage broadcasting
			// a PlaybillSchemaCue) should not be inside the DB transaction. Let-it-fail policy
			// (project_playbill_design.md) — if a subscriber throws, the INSERT is committed
			// and the exception propagates to the caller.
			OnSchemaRegistered?.Invoke(schemaName, declarations);
		}

		private string SelectDeclarations(SqlConnection connection, string schemaName)
		{
			string sql = $"SELECT Declarations FROM [{SCHEMAS_TABLE}] WHERE SchemaName = @SchemaName";
			using (var command = new SqlCommand(sql, connection))
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

			using (var connection = new SqlConnection(ConnectionString))
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
			string sql = $"SELECT SchemaName, Declarations FROM [{SCHEMAS_TABLE}] ORDER BY SchemaName";

			using (var connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new SqlCommand(sql, connection))
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

			string sql = $"INSERT INTO [{RECORDS_TABLE}] (EntryId, SchemaName, SerializedParameters) VALUES (@EntryId, @SchemaName, @SerializedParameters)";

			using (var connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@EntryId", entryId);
						command.Parameters.AddWithValue("@SchemaName", schemaName);
						command.Parameters.AddWithValue("@SerializedParameters", serializedParameters);
						command.ExecuteNonQuery();
					}
				}
				catch (SqlException e)
				{
					// 2627 = Violation of PRIMARY KEY, 2601 = Cannot insert duplicate
					// key row in object with unique index.
					if (e.Number == 2627 || e.Number == 2601)
					{
						throw new LanguageException($"Playbill record for EntryId {entryId} already exists (expected at most one per entry).");
					}
					Logger.Error($"WriteRecord entryId={entryId} schema='{schemaName}' on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to write Playbill record on SQL Server for EntryId {entryId}: {e.Message}");
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

			string sql = $"SELECT SchemaName, SerializedParameters FROM [{RECORDS_TABLE}] WHERE EntryId = @EntryId";

			using (var connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new SqlCommand(sql, connection))
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
			string sql = $"SELECT EntryId, SerializedParameters FROM [{RECORDS_TABLE}] WHERE SchemaName = @SchemaName ORDER BY EntryId ASC";

			using (var connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new SqlCommand(sql, connection))
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
			string sql = $"SELECT EntryId, SchemaName, SerializedParameters FROM [{RECORDS_TABLE}] WHERE EntryId > @AfterEntryId ORDER BY EntryId ASC";

			using (var connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (var command = new SqlCommand(sql, connection))
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

		// Rebuild-via-shadow-swap en SQL Server: SELECT INTO crea
		// PlaybillRecords_new con los records vivos (sin indices/constraints —
		// SELECT INTO solo copia datos), luego DROP la vieja y sp_rename la nueva
		// al nombre canonico. Recrea el indice secundario y el PK explicitamente.
		// Toda la operacion bajo una transaccion explicita para que la ausencia
		// de la tabla no sea observable por otros conexiones.
		internal override void Distill()
		{
			string newTable = RECORDS_TABLE + "_new";

			using (var connection = new SqlConnection(ConnectionString))
			{
				SqlTransaction tx = null;
				try
				{
					connection.Open();

					// Limpieza defensiva por si un Distill previo aborto.
					using (var dropCmd = new SqlCommand($"IF OBJECT_ID('{newTable}') IS NOT NULL DROP TABLE [{newTable}]", connection))
					{
						dropCmd.ExecuteNonQuery();
					}

					tx = connection.BeginTransaction();

					string selectInto = $@"
						SELECT pr.*
						INTO [{newTable}]
						FROM [{RECORDS_TABLE}] pr
						WHERE EXISTS (SELECT 1 FROM [{ActorName}] j WHERE j.id = pr.EntryId)";
					ExecuteNonQuery(connection, tx, selectInto);

					// SELECT INTO no copia PK / indices. Recrearlos sobre la sombra.
					ExecuteNonQuery(connection, tx, $"ALTER TABLE [{newTable}] ADD PRIMARY KEY (EntryId)");
					ExecuteNonQuery(connection, tx, $"CREATE INDEX IX_PlaybillRecords_SchemaName ON [{newTable}] (SchemaName)");

					ExecuteNonQuery(connection, tx, $"DROP TABLE [{RECORDS_TABLE}]");
					ExecuteNonQuery(connection, tx, $"EXEC sp_rename '{newTable}', '{RECORDS_TABLE}'");

					tx.Commit();
				}
				catch (SqlException e)
				{
					try { tx?.Rollback(); } catch { }
					Logger.Error($"Distill Playbill on actor '{ActorName}': {e.Message}", e);
					throw new LanguageException($"Failed to distill Playbill on SQL Server for actor '{ActorName}': {e.Message}");
				}
				finally
				{
					connection.Close();
				}
			}
		}

		private static void ExecuteNonQuery(SqlConnection connection, SqlTransaction tx, string sql)
		{
			using (var command = new SqlCommand(sql, connection, tx))
			{
				command.CommandType = CommandType.Text;
				command.ExecuteNonQuery();
			}
		}

		internal override MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			throw new NotImplementedException("Archive pending for PlaybillStoreSQLServer");
		}
	}
}
