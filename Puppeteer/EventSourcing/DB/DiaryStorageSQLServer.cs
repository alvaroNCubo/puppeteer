using Microsoft.Data.SqlClient;
using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class DiaryStorageSQLServer : DiaryStorage
	{
		private const string RETRY_TCP_TAG = "//TCP PROVIDER ERROR. IT IS A RETRY";

		private readonly string sqlServerWriteScriptCommand;
		private readonly string sqlServerWriteDefineCommand;
		private readonly string sqlServerWriteInvocationCommand;
		private readonly string sqlServerWriteScriptCommandWithExposeData;
		private readonly string sqlServerWriteDefineCommandWithExposeData;
		private readonly string sqlServerWriteInvocationCommandWithExposeData;
		// Phase 6 of the Action refactor: dropped sqlServerWriteActionCommand,
		// sqlServerWriteNewActionCommand, and their *WithExposeData variants. The
		// post-refactor write API is WriteScriptEntry / WriteDefineEntry /
		// WriteInvocationEntry / WriteDefineWithFirstInvocation. The legacy
		// _ACTION lateral table is gone.

		internal DiaryStorageSQLServer(IActorEventJournalClient eventJournalClient, string connectionString) : base(eventJournalClient, connectionString)
		{
			sqlServerWriteScriptCommand = "insert into " + Name + " (id, OccurredAt,Script) values (@id, @OccurredAt,@script)";

			// Define rows materialise the action's Statement directly inside the
			// journal row (script = canonical sentence, action = actionId,
			// arguments = NULL). The first invocation lives in a separate
			// Invocation row, so MarkAsSkip on a first invocation cannot
			// collaterally erase the Define.
			sqlServerWriteDefineCommand = $"INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@EntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL)";

			// Invocation rows: script = NULL, action = actionId, arguments = args.
			sqlServerWriteInvocationCommand = $"INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@EntryId, @OccurredAt, NULL, @ActionID, @Arguments)";

			sqlServerWriteScriptCommandWithExposeData = $@"
				{sqlServerWriteScriptCommand};
				INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@DiaryId, @ExposeJson);";

			sqlServerWriteDefineCommandWithExposeData = $@"
				{sqlServerWriteDefineCommand};
				INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@DiaryId, @ExposeJson);";

			sqlServerWriteInvocationCommandWithExposeData = $@"
				{sqlServerWriteInvocationCommand};
				INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@DiaryId, @ExposeJson);";

			eventElisionStorage = new EventElisionStorageSQLServer(eventJournalClient, connectionString);
			eventMaterializationStorage = new EventMaterializationStorageSQLServer(eventJournalClient, connectionString);
			materializationCheckpointStorage = new MaterializationCheckpointStorageSQLServer(eventJournalClient, connectionString);
		}

		private bool ExisteTabla(string tableName)
		{
			bool existe = false;
			string sql = $@"
				IF EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{tableName}')
					SELECT 1
				ELSE
					SELECT 0
			";
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						var resultado = (int)command.ExecuteScalar();
						existe = resultado == 1;
					}
				}
				catch
				{
					existe = false;
				}
				finally
				{
					connection.Close();
				}
			}
			return existe;
		}

		private async Task<bool> ExisteTablaAsync(string tableName)
		{
			bool existe = false;
			string sql = $@"
				IF EXISTS( SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{tableName}')
					SELECT 1
				ELSE
					SELECT 0
			";
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						var resultado = (int)await command.ExecuteScalarAsync();
						existe = resultado == 1;
					}
				}
				catch
				{
					existe = false;
				}
				finally
				{
					await connection.CloseAsync();
				}
			}
			return existe;
		}


		private async Task<bool> CreateDiaryAsync(string tableName)
		{
			bool created = false;
			StringBuilder statement = new StringBuilder();
			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES ")
				.Append("WHERE TABLE_NAME = '").Append(tableName).Append("')")
				.Append(" BEGIN")
				.Append("  create table ").Append(tableName)
				.Append("  (")
				.Append("  id BIGINT PRIMARY KEY,")
				.Append("  OccurredAt DATETIME NOT NULL,")
				.Append("  Script TEXT NULL,")
				.Append("  Action INT NULL,")
				.Append("  Arguments TEXT NULL,")
				.Append("  [Skip] BIT NOT NULL DEFAULT 0")
				.Append(" );")
				.Append(" SELECT 1 created;")
				.Append(" END")
				.Append(" ELSE")
				.Append(" BEGIN")
				.Append("  SELECT 0 created;")
				.Append(" END;\n");
			// Lab note: trailing newline + semicolon ensure SQL Edge's parser
			// terminates the IF/BEGIN/END before the next statement; without
			// the separator the concatenation reads as "ENDIF" which SQL
			// Edge (stricter than full SQL Server 2022) rejects.

			// Phase 6 of the Action refactor: dropped the CREATE TABLE _ACTION
			// schema. Action definitions live in the journal as Define records.

			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Reaction')")
				.Append(" BEGIN")
				.Append("	create table Reaction")
				.Append("	(")
				.Append("		Id INT NOT NULL PRIMARY KEY,")
				.Append("		Reaction NVARCHAR(MAX) NOT NULL")
				.Append("	);")
				.Append(" END;\n");

			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ReactionCheckpoint')")
				.Append(" BEGIN")
				.Append("	CREATE TABLE [dbo].[ReactionCheckpoint] (")
				.Append("		[ReactionId] INT NOT NULL,")
				.Append("		[Pattern] INT NOT NULL,")
				.Append("		[DiaryId] BIGINT NOT NULL DEFAULT 0,")
				.Append("		[ConfirmedDiaryId] BIGINT NOT NULL DEFAULT 0,")
				.Append("		PRIMARY KEY CLUSTERED ([ReactionId] ASC, [Pattern] ASC)")
				.Append("	);")
				.Append(" END;\n");

			// Resume optimization (checkpoint redesign, step 2): two global cursors per reaction.
			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ReactionFrontier')")
				.Append(" BEGIN")
				.Append("	CREATE TABLE [dbo].[ReactionFrontier] (")
				.Append("		[ReactionId] INT NOT NULL,")
				.Append("		[HighWater] BIGINT NOT NULL DEFAULT 0,")
				.Append("		[ClosedFrontier] BIGINT NOT NULL DEFAULT 0,")
				.Append("		PRIMARY KEY CLUSTERED ([ReactionId] ASC)")
				.Append("	);")
				.Append(" END;\n");

			statement.Append(@"
				IF NOT EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Follower')
				BEGIN
					create table Follower
					(
						FollowerId INT NOT NULL PRIMARY KEY,
						DiaryId BIGINT NOT NULL,
						Description VARCHAR(45) NOT NULL
					)
				END;
			");

			// ExposeData: optional lateral table (one row per event that executed expose).
			// The follower path forces includeExposeData=true and its replay SELECT
			// does a LEFT JOIN ExposeData, so the table must always exist — just like
			// Reaction/ReactionCheckpoint/ReactionFrontier/Follower. Reported by a follower deployment
			// 2.0.1-beta.9817. The column is DiaryId (not DairyId): it is the one the engine reads in
			// the JOIN and in the INSERT INTO ExposeData (DiaryId, ExposeJson).
			statement.Append(@"
				IF NOT EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ExposeData')
				BEGIN
					CREATE TABLE [dbo].[ExposeData] (
						[DiaryId] BIGINT NOT NULL PRIMARY KEY,
						[ExposeJson] NVARCHAR(MAX) NOT NULL
					)
				END;
			");

			string sql = statement.ToString();

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				await connection.OpenAsync();
				using (SqlCommand command = new SqlCommand(sql, connection))
				using (SqlDataReader reader = await command.ExecuteReaderAsync())
				{
					await reader.ReadAsync();
					created = reader.GetInt32(0) == 1;

					await reader.CloseAsync();
				}
				await connection.CloseAsync();
			}

			return created;
		}
		private bool CreateDiary(string tableName)
		{
			bool created = false;
			StringBuilder statement = new StringBuilder();
			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES ")
				.Append("WHERE TABLE_NAME = '").Append(tableName).Append("')")
				.Append(" BEGIN")
				.Append("	create table ").Append(tableName)
				.Append("	(")
				.Append("		id BIGINT PRIMARY KEY,")
				.Append("		OccurredAt DATETIME NOT NULL,")
				.Append("		Script TEXT NULL,")
				.Append("		Action INT NULL,")
				.Append("	    Arguments TEXT NULL,")
				.Append("		[Skip] BIT NOT NULL DEFAULT 0")
				.Append("	);")
				.Append("	SELECT 1 created;")
				.Append(" END")
				.Append(" ELSE")
				.Append(" BEGIN")
				.Append("	SELECT 0 created;")
				.Append(" END;\n");

			// Phase 6 of the Action refactor: dropped the CREATE TABLE _ACTION
			// schema (sync variant).

			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Reaction')")
				.Append(" BEGIN")
				.Append("	create table Reaction")
				.Append("	(")
				.Append("		Id INT NOT NULL PRIMARY KEY,")
				.Append("		Reaction NVARCHAR(MAX) NOT NULL")
				.Append("	);")
				.Append(" END;\n");

			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ReactionCheckpoint')")
				.Append(" BEGIN")
				.Append("	CREATE TABLE [dbo].[ReactionCheckpoint] (")
				.Append("		[ReactionId] INT NOT NULL,")
				.Append("		[Pattern] INT NOT NULL,")
				.Append("		[DiaryId] BIGINT NOT NULL DEFAULT 0,")
				.Append("		[ConfirmedDiaryId] BIGINT NOT NULL DEFAULT 0,")
				.Append("		PRIMARY KEY CLUSTERED ([ReactionId] ASC, [Pattern] ASC)")
				.Append("	);")
				.Append(" END;\n");

			// Resume optimization (checkpoint redesign, step 2): two global cursors per reaction.
			statement
				.Append("IF NOT EXISTS(")
				.Append("SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ReactionFrontier')")
				.Append(" BEGIN")
				.Append("	CREATE TABLE [dbo].[ReactionFrontier] (")
				.Append("		[ReactionId] INT NOT NULL,")
				.Append("		[HighWater] BIGINT NOT NULL DEFAULT 0,")
				.Append("		[ClosedFrontier] BIGINT NOT NULL DEFAULT 0,")
				.Append("		PRIMARY KEY CLUSTERED ([ReactionId] ASC)")
				.Append("	);")
				.Append(" END;\n");

			statement.Append(@"
				IF NOT EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Follower')
				BEGIN
					create table Follower
					(
						FollowerId INT NOT NULL PRIMARY KEY,
						DiaryId BIGINT NOT NULL,
						Description VARCHAR(45) NOT NULL
					)
				END;
			");

			// ExposeData: optional lateral table (one row per event that executed expose).
			// The follower path forces includeExposeData=true and its replay SELECT
			// does a LEFT JOIN ExposeData, so the table must always exist — just like
			// Reaction/ReactionCheckpoint/ReactionFrontier/Follower. Reported by a follower deployment
			// 2.0.1-beta.9817. The column is DiaryId (not DairyId): it is the one the engine reads in
			// the JOIN and in the INSERT INTO ExposeData (DiaryId, ExposeJson).
			statement.Append(@"
				IF NOT EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ExposeData')
				BEGIN
					CREATE TABLE [dbo].[ExposeData] (
						[DiaryId] BIGINT NOT NULL PRIMARY KEY,
						[ExposeJson] NVARCHAR(MAX) NOT NULL
					)
				END;
			");


			string sql = statement.ToString();

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				connection.Open();
				using (SqlCommand command = new SqlCommand(sql, connection))
				using (SqlDataReader reader = command.ExecuteReader())
				{
					reader.Read();
					created = reader.GetInt32(0) == 1;

					reader.Close();
				}
				connection.Close();
			}

			return created;
		}

		// Phase 6 of the Action refactor: dropped LoadSingleAction +
		// LoadActionsWithExitStatus (sync + async). The lateral _ACTION table is
		// gone; Define entries in the journal populate the cache via
		// AddKnownActionFromDefine.


		protected internal override long RehydrateFromEvent(long afterEntryId = 0, bool includeExposeData = false)
		{
			EventJournalClient.IsNew = CreateDiary(Name);

			bool canContinueReplay = false;
			long ultimoId = afterEntryId;
			long delta = 0;
			bool salir = false;
			int cantidadDeLeaderInitialization = 0;

			// Forward replay: events in ascending id order.
			string orderByClause = "ORDER BY d.id ASC";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					// Materialize v2 / Phase 0.5: the journal Skip column is authoritative
					// for rehydration. Replaces LEFT JOIN EventElision with WHERE d.[Skip] = 0.
					// EventElisionStorageSQLServer.MarkEventsAsElided sets Skip = 1 transactionally.
					string sqlCount = $@"
						SELECT Count_Big(*) Cantidad
						FROM {base.Name} d WITH (NOLOCK)
						WHERE d.[Skip] = 0 AND d.id > {ultimoId}";
					// Lab note: switched from COUNT(*) (INT) to COUNT_BIG(*)
					// (BIGINT) so reader.GetInt64 doesn't throw InvalidCastException
					// under SQL Edge (which returns INT strictly for COUNT(*));
					// full SQL Server 2022 was tolerant via implicit driver
					// conversion. Functionally identical at runtime.
					using (SqlCommand command = new SqlCommand(sqlCount, connection))
					using (SqlDataReader reader = command.ExecuteReader())
					{
						reader.Read();
						long totalDeRegistros = reader.GetInt64(0);
						EventJournalClient.BeginJournalReplay(totalDeRegistros);
						reader.Close();
					}
				}
				finally
				{
					connection.Close();
				}
			}

			try
			{
				long entryId = 0;

				while (!salir && (canContinueReplay = EventJournalClient.CanContinueReplay(entryId)))
				{
					// Phase 5 of the Action refactor: legacy LoadActionsWithExitStatus
					// (pre-load the _ACTION lateral table) is dropped. Define entries
					// in the journal populate the action cache in entry-id order via
					// AddKnownActionFromDefine — by construction Define precedes any
					// Invocation that references it, so the legacy pre-load path is
					// redundant and the lateral _ACTION table is no longer authoritative.
					// (Phase 6 cleanup deletes LoadActionsWithExitStatus itself, the
					// _ACTION table schema, and WriteNewActionEntry that populated it.)

					string sql = includeExposeData
						? $@"SELECT d.Id, d.OccurredAt, d.Script, d.Action, d.Arguments, ed.ExposeJson
							FROM {base.Name} d WITH (NOLOCK)
							LEFT JOIN ExposeData ed WITH (NOLOCK) ON d.id = ed.DiaryId
							WHERE d.[Skip] = 0 AND d.id > {ultimoId}
							{orderByClause}"
						: $@"SELECT d.Id, d.OccurredAt, d.Script, d.Action, d.Arguments
							FROM {base.Name} d WITH (NOLOCK)
							WHERE d.[Skip] = 0 AND d.id > {ultimoId}
							{orderByClause}";


					using (SqlConnection connection = new SqlConnection(ConnectionString))
					{
						try
						{
							connection.Open();

							using (SqlCommand command = new SqlCommand(sql, connection))
							using (SqlDataReader reader = command.ExecuteReader())
							{
								while (reader.Read() && (canContinueReplay = EventJournalClient.CanContinueReplay(entryId)))
								{
									entryId = reader.GetInt64(0);

									DateTime occurredAt = reader.GetDateTime(1);

									bool scriptIsNull = reader.IsDBNull(2);
									bool actionIsNull = reader.IsDBNull(3);

									// Phase 4 of the Action refactor: process Define rows by
									// parsing the canonical sentence and populating the
									// actionCommands cache. The lateral _ACTION table is no
									// longer the source of truth; the journal is. (Phase 5
									// drops the lateral table and the legacy load paths;
									// Phase 4 keeps them cohabiting.)
									if (!scriptIsNull && !actionIsNull)
									{
										int defineActionId = reader.GetInt32(3);
										string defineStatementText = reader.GetString(2);
										EventJournalClient.AddKnownActionFromDefine(defineActionId, defineStatementText);
										continue;
									}

									if (scriptIsNull)
									{
										int actionId = reader.GetInt32(3);

										// Phase 5 of the Action refactor: legacy
										// LoadSingleAction (recovery lookup against the
										// _ACTION lateral table) is dropped. Define entries
										// in the journal precede any Invocation that
										// references them by construction (atomic write of
										// Define + first Invocation, monotonic entry-id
										// ordering), so the cache is always populated by the
										// time we reach the Invocation row.

										string arguments = reader.GetString(4);
										string exposeJson = includeExposeData && !reader.IsDBNull(5) ? reader.GetString(5) : null;

										var actionData = EventDataPool.RentAction();
										actionData.EntryId = entryId;
										actionData.OccurredAt = occurredAt;
										actionData.ActionId = actionId;
										actionData.Arguments = arguments;
										actionData.ExposeData = exposeJson;

										EventJournalClient.ReplayEvent(actionData);
										// Lab note (paper05-lab4): the producer must NOT
										// Return() actionData here — the eventsQueue holds
										// only the reference, and EventDataPool.Return()
										// resets Arguments to null. The consumer task
										// (executionTask in ActorHandler.EventSourcingStorage)
										// is responsible for returning the EventData after
										// Perform completes (mirroring DiaryStorageMySQL,
										// where this Return call is intentionally absent).
									}
									else
									{
										string script = reader.GetString(2);
										string exposeJson = includeExposeData && !reader.IsDBNull(5) ? reader.GetString(5) : null;

										var scriptData = EventDataPool.RentScript();
										scriptData.EntryId = entryId;
										scriptData.OccurredAt = occurredAt;
										scriptData.Script = script;
										scriptData.ExposeData = exposeJson;

										EventJournalClient.ReplayEvent(scriptData);
										// See note above — Return() is deferred to the
										// consumer task; nulling Script here would
										// corrupt the still-queued reference.
									}
								}
								reader.Close();
							}
						}
						finally
						{
							connection.Close();
						}
					}

					delta = ultimoId - entryId;
					ultimoId = entryId;
					if (delta == 0)
					{
						const bool AT_LEAST_IS_NECESSARY_ONE_MORE_TIME = false;
						if (cantidadDeLeaderInitialization < 1)
						{
							EventJournalClient.EndJournalReplay(forcedToEnd: !canContinueReplay);
							salir = AT_LEAST_IS_NECESSARY_ONE_MORE_TIME;
							cantidadDeLeaderInitialization++;
						}
						else
						{
							salir = delta == 0;
						}
					}
					else
					{
						salir = delta == 0;
					}
				}
			}
			catch (Exception e) //As is inside a fire and forget, this is needed to see the error and execute the CompleteAdding unlocking the main process.
			{
				// Goes through IPuppeteerLogger.Error instead of Console.WriteLine: with the
				// default ConsoleLogger it goes to Console.Error (stderr) with prefix
				// [Puppeteer ERROR]. If the host injected a logger (Serilog/MEL/NLog)
				// via Performance.Logger(...), it receives it there too.
				Logger.Error($"RehydrateFromEvent failure on actor '{base.Name}'. type:{e.GetType()} error:{e.Message}", e);
			}

			return ultimoId;
		}

		protected internal override Task<long> RehydrateFromEventAsync(long afterEntryId, bool includeExposeData = false)
		{
			return Task.FromResult(RehydrateFromEvent(afterEntryId, includeExposeData));
		}

		protected internal override long GetLastProcessedEntryId(int followerId)
		{
			string sql = $"SELECT DiaryId FROM Follower WHERE FollowerId = {followerId}";
			int result = 0;
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					using (SqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							result = reader.GetInt32(0);
						}
						reader.Close();
					}
				}
				finally
				{
					connection.Close();
				}
			}
			return result;
		}

		protected internal override void SaveLastProcessedEntryId(int followerId, long lastEntryId)
		{
			if (followerId <= 0) throw new LanguageException("Follower Id must be upper than zero");
			if (lastEntryId <= 0) throw new LanguageException($"Last processed entry id '{lastEntryId}' must be greater than zero");

			long lastProcessedEntryId = EventJournalClient.GetLastProcessedEntryId(followerId);
			if (lastEntryId > lastProcessedEntryId)
			{
				string sql;
				if (lastProcessedEntryId != 0)
					sql = $"UPDATE Follower SET DiaryId = {lastEntryId} WHERE FollowerId = {followerId}";
				else
					sql = $"INSERT INTO Follower (FollowerId, DiaryId, Description) VALUES ({followerId}, {lastEntryId}, '{EventJournalClient.ActorName} Follower')";

				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.CommandType = CommandType.Text;
							command.ExecuteNonQuery();
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}
		}

		private string SqlCommand(string script, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
			StringBuilder statement = new StringBuilder()
				.Append("insert into ")
				.Append(base.Name)
				.Append("(OccurredAt,Script) values (")
					.Append('\'').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('\'')
					.Append(',')
					.Append('\'')
					.Append(script)
					.Append('\'')
				.Append(')');
			string sql = statement.ToString();
			return sql;
		}
		private string SqlCommand(long entryId, string script, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
			StringBuilder statement = new StringBuilder()
				.Append("insert into ")
				.Append(base.Name)
				.Append("(id, OccurredAt,Script) values (")
					.Append(entryId)
					.Append(',')
					.Append('\'').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('\'')
					.Append(',')
					.Append('\'')
					.Append(script)
					.Append('\'')
				.Append(')');
			string sql = statement.ToString();
			return sql;
		}

		// Phase 6 of the Action refactor: dropped the SqlCommand error-string
		// helpers for action / new-action paths — those write methods are gone.

		private string UnEscapeSQLServer(string script)
		{
			StringBuilder result = new StringBuilder();
			foreach (char c in script)
			{
				switch (c)
				{
					case LiteralString.SLASH_OR_SINGLE_QUOTED_CHARACTER:
						break;
					case LiteralString.DOUBLE_QUOTED_CHARACTER:
						result.Append('"');
						break;
					case LiteralString.PIPE_CHARACTER:
						result.Append('\\');
						break;
					default:
						result.Append(c);
						break;
				}
			}
			return result.ToString();
		}

		protected internal override async Task WriteScriptEntryAsync(long entryId, string originalScript, DateTime now, string exposeData = null)
		{
			string script = originalScript;
			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						await connection.OpenAsync();
						bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
						string sql = hasExposeData ? sqlServerWriteScriptCommandWithExposeData : sqlServerWriteScriptCommand;

						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@id", entryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@script", UnEscapeSQLServer(script));

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@DiaryId", entryId);
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							_ = await command.ExecuteNonQueryAsync();
						}
					}
					catch (SqlException e)
					{
						var sqlCommand = SqlCommand(entryId, script, now);
						Logger.Error($@"sql:{sqlCommand} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
							script = RETRY_TCP_TAG + '\r' + originalScript;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
							script = RETRY_TCP_TAG + '\r' + originalScript;
						}
						else
						{
							throw new Exception("Error al escribir en SQLServer el Script en el Diary: [" + sqlCommand + "]. " + e.Message);
						}
					}
					catch (Exception e)
					{
						var sqlCommand = SqlCommand(entryId, script, now);
						Logger.Error($@"sql:{sqlCommand} type:{e.GetType()} error:{e.Message}", e);
						throw;
					}
					finally
					{
						await connection.CloseAsync();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeScriptRecord(entryId, originalScript, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// Phase 6 of the Action refactor: dropped WriteActionEntryAsync,
		// WriteNewActionEntryAsync, and WriteActionEntry overrides. The
		// post-refactor write API is WriteScriptEntry / WriteDefineEntry /
		// WriteInvocationEntry / WriteDefineWithFirstInvocation.

		protected internal override void WriteScriptEntry(long entryId, string originalScript, DateTime now, string exposeData = null)
		{
			string script = originalScript;
			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
						string sql = hasExposeData ? sqlServerWriteScriptCommandWithExposeData : sqlServerWriteScriptCommand;

						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.Add("@id", SqlDbType.BigInt);
							command.Parameters.Add("@OccurredAt", SqlDbType.DateTime);
							command.Parameters.Add("@script", SqlDbType.Text);

							command.Parameters["@id"].Value = entryId;
							command.Parameters["@OccurredAt"].Value = now;
							command.Parameters["@script"].Value = UnEscapeSQLServer(script);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@DiaryId", entryId);
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							command.ExecuteNonQuery();
						}
					}
					catch (SqlException e)
					{
						var sqlCommand = SqlCommand(entryId, script, now);
						Logger.Error($@"sql:{sqlCommand} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
							script = RETRY_TCP_TAG + '\r' + originalScript;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
							script = RETRY_TCP_TAG + '\r' + originalScript;
						}
						else
						{
							throw new Exception("Error al escribir en SQLServer el Script en el Diary: [" + sqlCommand + "]. " + e.Message);
						}
					}
					catch (Exception e)
					{
						var sqlCommand = SqlCommand(entryId, script, now);
						Logger.Error($@"sql:{sqlCommand} type:{e.GetType()} error:{e.Message}", e);
						throw;
					}
					finally
					{
						connection.Close();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeScriptRecord(entryId, originalScript, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}


		// Phase 6 of the Action refactor: dropped WriteNewActionEntry override.

		// Phase 3 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// new write APIs for the post-cutover path. WriteDefineEntry inserts a row in
		// the journal with `script` populated by the canonical Define sentence and
		// `action` populated by actionId — the post-cutover discriminator (script
		// != NULL ∧ action != NULL → Define). Crucially, NO INSERT into the lateral
		// _ACTION table — that is the legacy WriteNewActionEntry's job, and Phase 6
		// drops the table entirely. Replay silently skips Define rows in Phase 3
		// (see the discriminator update at the top of RehydrateFromEvent).
		protected internal override void WriteDefineEntry(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);

			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
						string sql = hasExposeData ? sqlServerWriteDefineCommandWithExposeData : sqlServerWriteDefineCommand;

						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@EntryId", entryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@DefineStatementText", defineStatementText);
							command.Parameters.AddWithValue("@ActionID", actionId);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@DiaryId", entryId);
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							command.ExecuteNonQuery();
						}
					}
					catch (SqlException e)
					{
						Logger.Error($@"WriteDefineEntry actionId:{actionId} entryId:{entryId} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
						}
						else
						{
							throw new Exception($"Error al escribir Define entry en SQLServer (actionId={actionId}, entryId={entryId}). {e.Message}");
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeDefineRecord(actionId, defineStatementText, entryId, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		protected internal override async Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);

			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						await connection.OpenAsync();
						bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
						string sql = hasExposeData ? sqlServerWriteDefineCommandWithExposeData : sqlServerWriteDefineCommand;

						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@EntryId", entryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@DefineStatementText", defineStatementText);
							command.Parameters.AddWithValue("@ActionID", actionId);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@DiaryId", entryId);
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							_ = await command.ExecuteNonQueryAsync();
						}
					}
					catch (SqlException e)
					{
						Logger.Error($@"WriteDefineEntryAsync actionId:{actionId} entryId:{entryId} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
						}
						else
						{
							throw new Exception($"Error al escribir Define entry async en SQLServer (actionId={actionId}, entryId={entryId}). {e.Message}");
						}
					}
					finally
					{
						await connection.CloseAsync();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeDefineRecord(actionId, defineStatementText, entryId, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// WriteInvocationEntry: script = NULL, action = actionId, arguments = args.
		// Post-Phase-6 implementation (no longer delegates to the dropped
		// WriteActionEntry).
		protected internal override void WriteInvocationEntry(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(arguments);

			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
						string sql = hasExposeData ? sqlServerWriteInvocationCommandWithExposeData : sqlServerWriteInvocationCommand;

						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@EntryId", entryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@ActionID", actionId);
							command.Parameters.AddWithValue("@Arguments", arguments);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@DiaryId", entryId);
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							command.ExecuteNonQuery();
						}
					}
					catch (SqlException e)
					{
						Logger.Error($@"WriteInvocationEntry actionId:{actionId} entryId:{entryId} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
						}
						else
						{
							throw new Exception($"Error al escribir Invocation entry en SQLServer (actionId={actionId}, entryId={entryId}). {e.Message}");
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeInvocationRecord(actionId, entryId, now, arguments, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		protected internal override async Task WriteInvocationEntryAsync(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(arguments);

			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						await connection.OpenAsync();
						bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
						string sql = hasExposeData ? sqlServerWriteInvocationCommandWithExposeData : sqlServerWriteInvocationCommand;

						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@EntryId", entryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@ActionID", actionId);
							command.Parameters.AddWithValue("@Arguments", arguments);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@DiaryId", entryId);
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							_ = await command.ExecuteNonQueryAsync();
						}
					}
					catch (SqlException e)
					{
						Logger.Error($@"WriteInvocationEntryAsync actionId:{actionId} entryId:{entryId} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
						}
						else
						{
							throw new Exception($"Error al escribir Invocation entry async en SQLServer (actionId={actionId}, entryId={entryId}). {e.Message}");
						}
					}
					finally
					{
						await connection.CloseAsync();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeInvocationRecord(actionId, entryId, now, arguments, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// Phase 4 atomic write — see DiaryStorage.cs for the contract. The two INSERTs
		// run inside a single multi-statement SqlCommand. SQL Server treats a single
		// command's statements as a transactional unit by default with implicit
		// transaction semantics (auto-commit per command); the two INSERTs either
		// both succeed or both fail.
		protected internal override void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);
			ArgumentNullException.ThrowIfNull(arguments);

			bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
			string sql = hasExposeData
				? $@"
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);
					INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@InvocationEntryId, @ExposeJson);"
				: $@"
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);";

			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@DefineEntryId", defineEntryId);
							command.Parameters.AddWithValue("@InvocationEntryId", invocationEntryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@DefineStatementText", defineStatementText);
							command.Parameters.AddWithValue("@ActionID", actionId);
							command.Parameters.AddWithValue("@Arguments", arguments);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							command.ExecuteNonQuery();
						}
					}
					catch (SqlException e)
					{
						Logger.Error($@"WriteDefineWithFirstInvocation actionId:{actionId} defineEntryId:{defineEntryId} invocationEntryId:{invocationEntryId} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
						}
						else
						{
							throw new Exception($"Error al escribir Define+Invocation atomic en SQLServer (actionId={actionId}, defineEntryId={defineEntryId}, invocationEntryId={invocationEntryId}). {e.Message}");
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] defineRecord = EncodeDefineRecord(actionId, defineStatementText, defineEntryId, now, null);
				byte[] invocationRecord = EncodeInvocationRecord(actionId, invocationEntryId, now, arguments, exposeData);
				OnRecordWritten.Invoke(defineEntryId, defineRecord);
				OnRecordWritten.Invoke(invocationEntryId, invocationRecord);
			}
		}

		protected internal override async Task WriteDefineWithFirstInvocationAsync(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);
			ArgumentNullException.ThrowIfNull(arguments);

			bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
			string sql = hasExposeData
				? $@"
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);
					INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@InvocationEntryId, @ExposeJson);"
				: $@"
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO {Name} (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);";

			bool retry;
			do
			{
				retry = false;
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						await connection.OpenAsync();
						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@DefineEntryId", defineEntryId);
							command.Parameters.AddWithValue("@InvocationEntryId", invocationEntryId);
							command.Parameters.AddWithValue("@OccurredAt", now);
							command.Parameters.AddWithValue("@DefineStatementText", defineStatementText);
							command.Parameters.AddWithValue("@ActionID", actionId);
							command.Parameters.AddWithValue("@Arguments", arguments);

							if (hasExposeData)
							{
								command.Parameters.AddWithValue("@ExposeJson", exposeData);
							}

							_ = await command.ExecuteNonQueryAsync();
						}
					}
					catch (SqlException e)
					{
						Logger.Error($@"WriteDefineWithFirstInvocationAsync actionId:{actionId} defineEntryId:{defineEntryId} invocationEntryId:{invocationEntryId} type:{e.GetType()} error:{e.Message}", e);

						if (e.Message.Contains("A transport-level error has occurred when receiving results from the server."))
						{
							retry = true;
						}
						else if (e.Message.Contains("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is configured to allow remote connections."))
						{
							retry = true;
						}
						else
						{
							throw new Exception($"Error al escribir Define+Invocation atomic async en SQLServer (actionId={actionId}, defineEntryId={defineEntryId}, invocationEntryId={invocationEntryId}). {e.Message}");
						}
					}
					finally
					{
						await connection.CloseAsync();
					}
				}
			}
			while (retry);

			if (OnRecordWritten != null)
			{
				byte[] defineRecord = EncodeDefineRecord(actionId, defineStatementText, defineEntryId, now, null);
				byte[] invocationRecord = EncodeInvocationRecord(actionId, invocationEntryId, now, arguments, exposeData);
				OnRecordWritten.Invoke(defineEntryId, defineRecord);
				OnRecordWritten.Invoke(invocationEntryId, invocationRecord);
			}
		}

		internal override void ChangePrimaryKey()
		{
			string stCommand = $@"
							sp_rename '{Name}', '{Name}_old';

							create table {Name}
							(
								id INT PRIMARY KEY,
								OccurredAt DATETIME NOT NULL,
								Script TEXT NOT NULL,
								[Skip] BIT NOT NULL DEFAULT 0
							);

							INSERT INTO {Name}
							SELECT * FROM {Name}_old;

							--Its commented
							--DROP TABLE {Name}_old;

						";
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(stCommand, connection))
					{
						command.ExecuteNonQuery();
					}
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{stCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override MemoryStream Archive(DateTime fechaInicio, DateTime fechaFin)
		{

			IEnumerable<string> actorsNames = ListActorNames(Name);
			if (actorsNames == null) return null;

			MemoryStream compressedFileForArchive = new MemoryStream();
			ZipArchive archive = new ZipArchive(compressedFileForArchive, ZipArchiveMode.Create, false);


			StringBuilder insertString = new StringBuilder();

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				insertString.Append("USE ");
				insertString.Append(connection.Database);
				insertString.Append("\nGO \n");

				try
				{
					connection.Open();
					foreach (var aName in actorsNames)
					{
						msDairyPeriodRangeToExport = new MemoryStream();
						swDairyPeriodRangeToExport = new StreamWriter(msDairyPeriodRangeToExport, Encoding.UTF8);

						var fileName = aName + "-" + fechaFin.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_bak.sql";
						string sql = "SELECT OccurredAt, Script, Skip, Id FROM " + aName + " WITH (nolock) WHERE OccurredAt >= '" + fechaInicio + "' AND OccurredAt < '" + fechaFin + "' AND Skip = 1 ORDER BY id";
						using (SqlCommand command = new SqlCommand(sql, connection))
						using (SqlDataReader reader = command.ExecuteReader())
						{
							if (!reader.HasRows) continue;

							command.CommandTimeout = 60;
							insertString.Append("IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '");
							insertString.Append(aName);
							insertString.AppendLine("')");
							insertString.AppendLine("BEGIN");
							insertString.Append("PRINT 'Table ");
							insertString.Append('"');
							insertString.Append(aName);
							insertString.Append('"');
							insertString.Append(" does not exists.");
							insertString.AppendLine("';");
							insertString.AppendLine("SET noexec on;");
							insertString.AppendLine("END");

							while (reader.Read())
							{
								DateTime occurredAt = reader.GetDateTime(0);
								string script = reader.GetString(1);

								byte skip = Convert.ToByte(reader.GetBoolean(2));
								int id = reader.GetInt32(3);

								insertString.Append("INSERT [dbo].");
								insertString.Append(aName);
								insertString.Append(" ([OccurredAt], [Script], [Skip], [id]) VALUES (CAST(N'");
								insertString.Append(occurredAt);
								insertString.Append("' AS DateTime), N'");
								insertString.Append(script.Replace("'", "''"));
								insertString.Append("', ");
								insertString.Append(skip);
								insertString.Append(", ");
								insertString.Append(id);
								insertString.Append(')');
								insertString.Append("\nGO \n");

								swDairyPeriodRangeToExport.Write(insertString);
								insertString.Clear();
							}
							insertString.AppendLine("SET noexec off;");
							swDairyPeriodRangeToExport.Write(insertString);
							insertString.Clear();
							reader.Close();

							if (msDairyPeriodRangeToExport != null)
							{
								SaveTempFileToZip(archive, fileName);
							}
						}

					}
				}
				finally
				{
					connection.Close();
					archive.Dispose();
					compressedFileForArchive.Dispose();
				}
			}

			return compressedFileForArchive;
		}

		protected override internal IEnumerable<string> ListActorNames(string name)
		{
			List<string> actors = new List<string>();

			var existActor = ExisteTabla(name);
			if (existActor && name != "general")
			{
				actors.Add(name);
			}
			else if (ExisteTabla("general"))
			{
				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					StringBuilder sql = new StringBuilder();

					sql.Append("SELECT TABLE_NAME FROM ");
					sql.Append(connection.Database);
					sql.Append(".INFORMATION_SCHEMA.TABLES WHERE (TABLE_NAME LIKE 'C%' AND TABLE_NAME NOT LIKE 'C%[_]%') ORDER BY TABLE_NAME ASC;");
					try
					{
						connection.Open();
						using (SqlCommand command = new SqlCommand(sql.ToString(), connection))
						using (SqlDataReader reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								actors.Add(reader.GetString(0));
							}
							reader.Close();
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}

			return actors;
		}

		protected internal override void Trim(DateTime trimmedDown)
		{
			IEnumerable<string> actorsNames = ListActorNames(Name);

			StringBuilder sql = new StringBuilder();
			const string POSTFIX = "_$OLD";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					foreach (var aName in actorsNames)
					{
						bool needsTrim = NeedsTrim(aName, trimmedDown);

						if (needsTrim)
						{
							/*BUILDS THE RENAME SCRIPT*/
							sql.Append("EXEC sp_rename '");
							sql.Append(aName);
							sql.Append("', '");
							sql.Append(aName);
							sql.Append(POSTFIX);
							sql.Append("';");

							using (SqlCommand command = new SqlCommand(sql.ToString(), connection))
							{
								command.ExecuteNonQuery();
							}

							sql.Clear();

							if (!ExisteTabla(aName))
							{
								CreateDiary(aName);
							}

							/*BUILDS THE CREATE TABLE, INDEXES AND DATA SCRIPT*/
							sql.Append("INSERT INTO ");
							sql.Append(aName);
							sql.Append("(id, OccurredAt, Script, [Skip]) ");
							sql.Append("SELECT id, OccurredAt, Script, [Skip]");
							sql.Append(" FROM ");
							sql.Append(aName);
							sql.Append(POSTFIX);
							sql.Append(" WITH(NOLOCK) WHERE (Skip = 0");
							sql.Append(" AND OccurredAt < '");
							sql.Append(trimmedDown);
							sql.Append("') OR OccurredAt >= '");
							sql.Append(trimmedDown);
							sql.Append("' ORDER BY id;");

							/*BUILDS THE DROP TABLE SCRIPT*/
							sql.Append("DROP TABLE ");
							sql.Append(aName);
							sql.Append(POSTFIX);
							sql.Append(';');

							using (SqlCommand command = new SqlCommand(sql.ToString(), connection))
							{
								command.CommandTimeout = 600;
								command.ExecuteNonQuery();
							}
						}
					}
				}
				catch (SqlException e)
				{
					Logger.Error($@"sql:{sql} type:{e.GetType()} error:{e.Message}", e);

					throw new Exception("Error al escribir en SQLServer el Script en el Diary: [" + sql + "]. " + e.Message);
				}
				finally
				{
					connection.Close();
					sql.Clear();
				}
			}
		}

		private bool NeedsTrim(string aName, DateTime trimmedDown)
		{
			bool needsTrim = false;

			string sql = $"SELECT TOP 1 * FROM {aName} WITH(NOLOCK) WHERE Skip = 1 AND OccurredAt > {trimmedDown};";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql.ToString(), connection))
					using (SqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							needsTrim = true;
						}
						reader.Close();
					}
				}
				catch
				{
				}
				finally
				{
					connection.Close();
				}
			}

			return needsTrim;
		}

		protected internal static IEnumerable<string> GetActorsToLoad(string connectionString, double minimumContributionPercent)
		{
			throw new NotImplementedException();
		}

		protected internal override long GetOrCreateReactionId(string formattedReaction)
		{
			ArgumentNullException.ThrowIfNull(formattedReaction);

			// First try to obtain the ID if it already exists
			string selectSql = "SELECT Id FROM Reaction WHERE Reaction = @FormattedReaction";
			long existingId = 0;

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					using (SqlCommand command = new SqlCommand(selectSql, connection))
					{
						command.Parameters.AddWithValue("@FormattedReaction", formattedReaction);
						using (SqlDataReader reader = command.ExecuteReader())
						{
							if (reader.Read())
							{
								existingId = reader.GetInt32(0);
							}
							reader.Close();
						}
					}

					if (existingId != 0)
					{
						return existingId;
					}

					// Does not exist: generate a new ID and insert.
					// Get the current maximum Id.
					string maxIdSql = "SELECT ISNULL(MAX(Id), 0) FROM Reaction";
					int newId = 0;
					using (SqlCommand command = new SqlCommand(maxIdSql, connection))
					{
						object result = command.ExecuteScalar();
						newId = Convert.ToInt32(result) + 1;
					}

					// Insert the new reaction
					string insertSql = "INSERT INTO Reaction (Id, Reaction) VALUES (@ReactionId, @FormattedReaction)";
					using (SqlCommand command = new SqlCommand(insertSql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", newId);
						command.Parameters.AddWithValue("@FormattedReaction", formattedReaction);
						command.ExecuteNonQuery();
					}

					existingId = newId;
				}
				finally
				{
					connection.Close();
				}
			}

			return existingId;
		}

		// DEPRECATED: Kept for compatibility, returns Detected
		protected internal override long GetReactionLastProcessedEntryId(long reactionId, int pattern)
		{
			var (detected, _) = GetReactionCheckpoint(reactionId, pattern);
			return detected; // Return only Detected for compatibility
		}

		// DEPRECATED: Kept for compatibility, saves both (detected = confirmed = lastEntryId)
		protected internal override void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long lastEntryId)
		{
			if (pattern < 0) throw new LanguageException($"Pattern '{pattern}' must be zero or greater");
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");
			if (lastEntryId <= 0) throw new LanguageException($"Last processed entry id '{lastEntryId}' must be greater than zero");

			var (detected, _) = GetReactionCheckpoint(reactionId, pattern);

			if (lastEntryId > detected)
			{
				string sql;
				if (detected != 0)
					sql = "UPDATE ReactionCheckpoint SET DiaryId = @DiaryId, ConfirmedDiaryId = @DiaryId WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
				else
					sql = "INSERT INTO ReactionCheckpoint (ReactionId, Pattern, DiaryId, ConfirmedDiaryId) VALUES (@ReactionId, @Pattern, @DiaryId, @DiaryId)";

				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@ReactionId", reactionId);
							command.Parameters.AddWithValue("@Pattern", pattern);
							command.Parameters.AddWithValue("@DiaryId", lastEntryId);
							command.ExecuteNonQuery();
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}
		}

		// PHASE 5A: Two-phase checkpoint - returns tuple (detected, confirmed) in a single access
		// NOTE: The DiaryId column is used for Detected, and ConfirmedDiaryId for Confirmed
		// If ConfirmedDiaryId is NULL, return 0
		protected internal override (long detected, long confirmed) GetReactionCheckpoint(long reactionId, int seekLevel)
		{
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");
			if (seekLevel < 0) throw new LanguageException($"SeekLevel '{seekLevel}' must be zero or greater");

			string sql = "SELECT ISNULL(DiaryId, 0), ISNULL(ConfirmedDiaryId, 0) FROM ReactionCheckpoint WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
			long detected = 0;
			long confirmed = 0;

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
						command.Parameters.AddWithValue("@Pattern", seekLevel);
						using (SqlDataReader reader = command.ExecuteReader())
						{
							if (reader.Read())
							{
								detected = reader.GetInt64(0);
								confirmed = reader.GetInt64(1);
							}
							reader.Close();
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}

			return (detected, confirmed);
		}

		// Resume optimization (step 2): two global cursors per reaction (read-frontier +
		// closed-frontier). SQL Server is a "local journal" backend (Job/Cue row of the matrix)
		// -> resume by re-reading [closed, high-water]; does not use a snapshot.
		protected internal override (long highWater, long closedFrontier) GetReactionFrontier(long reactionId)
		{
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");

			string sql = "SELECT ISNULL(HighWater, 0), ISNULL(ClosedFrontier, 0) FROM ReactionFrontier WHERE ReactionId = @ReactionId";
			long highWater = 0;
			long closedFrontier = 0;

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
							if (reader.Read())
							{
								highWater = reader.GetInt64(0);
								closedFrontier = reader.GetInt64(1);
							}
							reader.Close();
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}

			return (highWater, closedFrontier);
		}

		protected internal override void SaveReactionFrontier(long reactionId, long highWater, long closedFrontier)
		{
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");
			if (highWater < 0) throw new LanguageException($"HighWater '{highWater}' must be zero or greater");
			if (closedFrontier < 0) throw new LanguageException($"ClosedFrontier '{closedFrontier}' must be zero or greater");

			string sql = @"
				IF EXISTS (SELECT 1 FROM ReactionFrontier WHERE ReactionId = @ReactionId)
					UPDATE ReactionFrontier SET HighWater = @HighWater, ClosedFrontier = @ClosedFrontier WHERE ReactionId = @ReactionId
				ELSE
					INSERT INTO ReactionFrontier (ReactionId, HighWater, ClosedFrontier) VALUES (@ReactionId, @HighWater, @ClosedFrontier)";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
						command.Parameters.AddWithValue("@HighWater", highWater);
						command.Parameters.AddWithValue("@ClosedFrontier", closedFrontier);
						command.ExecuteNonQuery();
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		// PHASE 5A: only save Confirmed after PerformCommand executes successfully.
		protected internal override void SaveReactionConfirmedCheckpoint(long reactionId, int seekLevel, long entryId)
		{
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");
			if (seekLevel < 0) throw new LanguageException($"SeekLevel '{seekLevel}' must be zero or greater");
			if (entryId <= 0) throw new LanguageException($"Entry id '{entryId}' must be greater than zero");

			// Check whether the row exists.
			var (detected, currentConfirmed) = GetReactionCheckpoint(reactionId, seekLevel);

			// Only update when the new entryId is greater than the current Confirmed.
			if (entryId > currentConfirmed)
			{
				string sql;
				if (detected != 0)
				{
					// Row exists: update only ConfirmedDiaryId.
					sql = "UPDATE ReactionCheckpoint SET ConfirmedDiaryId = @ConfirmedDiaryId WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
				}
				else
				{
					// Row does not exist: insert both (this should NOT happen in normal use).
					sql = "INSERT INTO ReactionCheckpoint (ReactionId, Pattern, DiaryId, ConfirmedDiaryId) VALUES (@ReactionId, @Pattern, @ConfirmedDiaryId, @ConfirmedDiaryId)";
				}

				using (SqlConnection connection = new SqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (SqlCommand command = new SqlCommand(sql, connection))
						{
							command.Parameters.AddWithValue("@ReactionId", reactionId);
							command.Parameters.AddWithValue("@Pattern", seekLevel);
							command.Parameters.AddWithValue("@ConfirmedDiaryId", entryId);
							command.ExecuteNonQuery();
						}
					}
					finally
					{
						connection.Close();
					}
				}
			}
		}

		protected internal override bool MarkEventsAsElidedWithCheckpoint(Follower.CheckpointCommit commit)
		{
			ArgumentNullException.ThrowIfNull(commit);

			long reactionId = commit.ReactionId;
			long[] eventIds = commit.EventIds;
			DateTime timestamp = commit.Timestamp;
			Follower.CheckpointVector newCheckpoint = commit.CheckpointVector;

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				connection.Open();
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					try
					{
						// Lexicographic comparison: allow matches that share events.
						bool isGreater = false;
						for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
						{
							long newEntryId = newCheckpoint.Get(seekLevel);

							string checkSql = "SELECT ISNULL(DiaryId, 0) FROM ReactionCheckpoint WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
							long currentEntryId = 0;

							using (SqlCommand checkCmd = new SqlCommand(checkSql, connection, transaction))
							{
								checkCmd.Parameters.AddWithValue("@ReactionId", reactionId);
								checkCmd.Parameters.AddWithValue("@Pattern", seekLevel);
								var result = checkCmd.ExecuteScalar();
								if (result != null && result != DBNull.Value)
								{
									currentEntryId = Convert.ToInt64(result);
								}
							}

							if (newEntryId > currentEntryId)
							{
								isGreater = true;
								break; // First differing level and greaterThan
							}
							else if (newEntryId < currentEntryId)
							{
								transaction.Rollback();
								return false; // First differing level and lessThan
							}
							// If equal, continue
						}

						if (!isGreater)
						{
							transaction.Rollback();
							return false; // All equal or lessThan
						}

						foreach (long eventId in eventIds)
						{
							string insertElisionSql = @"
								IF NOT EXISTS (SELECT 1 FROM EventElision WHERE DiaryId = @DiaryId)
								INSERT INTO EventElision (DiaryId, ReactionId, Timestamp) VALUES (@DiaryId, @ReactionId, @Timestamp)";

							using (SqlCommand insertCmd = new SqlCommand(insertElisionSql, connection, transaction))
							{
								insertCmd.Parameters.AddWithValue("@DiaryId", eventId);
								insertCmd.Parameters.AddWithValue("@ReactionId", reactionId);
								insertCmd.Parameters.AddWithValue("@Timestamp", timestamp);
								insertCmd.ExecuteNonQuery();
							}
						}

						// Phase 5A: save ONLY Detected (Confirmed is saved after PerformCommand succeeds).
						for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
						{
							long newDetected = newCheckpoint.Get(seekLevel);

							string upsertCheckpointSql = @"
								IF EXISTS (SELECT 1 FROM ReactionCheckpoint WHERE ReactionId = @ReactionId AND Pattern = @Pattern)
									UPDATE ReactionCheckpoint SET DiaryId = @DiaryId WHERE ReactionId = @ReactionId AND Pattern = @Pattern
								ELSE
									INSERT INTO ReactionCheckpoint (ReactionId, Pattern, DiaryId, ConfirmedDiaryId) VALUES (@ReactionId, @Pattern, @DiaryId, 0)";

							using (SqlCommand upsertCmd = new SqlCommand(upsertCheckpointSql, connection, transaction))
							{
								upsertCmd.Parameters.AddWithValue("@ReactionId", reactionId);
								upsertCmd.Parameters.AddWithValue("@Pattern", seekLevel);
								upsertCmd.Parameters.AddWithValue("@DiaryId", newDetected);
								upsertCmd.ExecuteNonQuery();
							}
						}

						transaction.Commit();
						return true;
					}
					catch
					{
						transaction.Rollback();
						throw;
					}
				}
			}
		}

		// Stage 5 of the Distill refactor. Same pattern as DiaryStorageMySQL.Distill
		// (see doc there) — single transaction, preserve MAX(id) (the "last
		// record" invariant), deletes `{Name}` rows and their corresponding entry in EventElision.
		// Uses a table variable @ToDistill (T-SQL) instead of CREATE TEMPORARY TABLE
		// (MySQL syntax).
		//
		// EventElision serves a single actor — the "one actor per database" principle
		// (see memory project_actor_per_db_principle.md) guarantees that all rows
		// belong to this actor. The INNER JOIN with `{Name}` is defensive, not
		// necessary for cross-actor isolation.
		protected internal override void Distill()
		{
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				connection.Open();
				using (SqlTransaction transaction = connection.BeginTransaction())
				{
					try
					{
						// Snapshot of the max id (preserved for the invariant).
						long maxId;
						string maxIdSql = $"SELECT ISNULL(MAX(id), 0) FROM {Name}";
						using (SqlCommand cmd = new SqlCommand(maxIdSql, connection, transaction))
						{
							object result = cmd.ExecuteScalar();
							maxId = (result == null || result == DBNull.Value) ? 0 : Convert.ToInt64(result);
						}

						if (maxId == 0)
						{
							// Empty journal, nothing to distill.
							transaction.Commit();
							return;
						}

						// Capture IDs to remove in a table variable, then two DELETEs
						// against that table. Scoped to this actor's journal (defensive).
						string distillSql = $@"
							DECLARE @ToDistill TABLE (id BIGINT PRIMARY KEY);

							INSERT INTO @ToDistill
							SELECT j.id FROM {Name} j
							INNER JOIN EventElision e ON j.id = e.DiaryId
							WHERE j.id <> @MaxId;

							DELETE FROM EventElision
							WHERE DiaryId IN (SELECT id FROM @ToDistill);

							DELETE FROM {Name}
							WHERE id IN (SELECT id FROM @ToDistill);";

						using (SqlCommand cmd = new SqlCommand(distillSql, connection, transaction))
						{
							cmd.Parameters.AddWithValue("@MaxId", maxId);
							cmd.CommandTimeout = 300;
							cmd.ExecuteNonQuery();
						}

						transaction.Commit();
					}
					catch
					{
						try { transaction.Rollback(); } catch { }
						throw;
					}
				}
			}
		}

		// Paper 5 / Materialize v2 — Phase 2. Read raw records (without filtering [Skip]).
		// Wire layer 1. Discriminator per Phase 6 of the Action refactor:
		// Script != NULL AND Action IS NULL = Script; Script != NULL AND Action != NULL =
		// Define; Script IS NULL AND Action != NULL = Invocation.
		protected internal override void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();

			string sql = $@"
				SELECT d.id, d.OccurredAt, d.Script, d.Action, d.Arguments, ed.ExposeJson
				FROM {Name} d WITH (NOLOCK)
				LEFT JOIN ExposeData ed WITH (NOLOCK) ON d.id = ed.DiaryId
				WHERE d.id > @afterId
				ORDER BY d.id ASC";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@afterId", afterEntryId);
						using (SqlDataReader reader = command.ExecuteReader())
						{
							while (reader.Read())
							{
								result.Add(MapRowToRecord(reader));
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

		protected internal override async Task ReadRecordsAfterAsync(long afterEntryId, List<MaterializationRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();

			string sql = $@"
				SELECT d.id, d.OccurredAt, d.Script, d.Action, d.Arguments, ed.ExposeJson
				FROM {Name} d WITH (NOLOCK)
				LEFT JOIN ExposeData ed WITH (NOLOCK) ON d.id = ed.DiaryId
				WHERE d.id > @afterId
				ORDER BY d.id ASC";

			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (SqlCommand command = new SqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@afterId", afterEntryId);
						using (SqlDataReader reader = await command.ExecuteReaderAsync())
						{
							while (await reader.ReadAsync())
							{
								result.Add(MapRowToRecord(reader));
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

		// Materialize v2 / Phase 3 — wire verb (c) DameCheckpointsHasta.
		protected internal override void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			string sql = "SELECT Id, Reaction FROM Reaction WITH (NOLOCK) ORDER BY Id";
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					using (SqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							long id = reader.GetInt32(0);
							string reactionText = reader.GetString(1);
							result.Add(new MaterializationReactionDefinition(id, reactionText));
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		protected internal override void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			string sql = "SELECT ReactionId, Pattern, DiaryId, ConfirmedDiaryId FROM ReactionCheckpoint WITH (NOLOCK) ORDER BY ReactionId, Pattern";
			using (SqlConnection connection = new SqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (SqlCommand command = new SqlCommand(sql, connection))
					using (SqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							long reactionId = reader.GetInt32(0);
							int seekLevel = reader.GetInt32(1);
							long detected = reader.GetInt64(2);
							long confirmed = reader.GetInt64(3);
							result.Add(new MaterializationReactionCheckpoint(reactionId, seekLevel, detected, confirmed));
						}
					}
				}
				finally
				{
					connection.Close();
				}
			}
		}

		private static MaterializationRecord MapRowToRecord(SqlDataReader reader)
		{
			long entryId = reader.GetInt64(0);
			DateTime occurredAt = reader.GetDateTime(1);
			string script = reader.IsDBNull(2) ? null : reader.GetString(2);
			int? actionId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
			string arguments = reader.IsDBNull(4) ? null : reader.GetString(4);
			string exposeData = reader.IsDBNull(5) ? null : reader.GetString(5);

			if (script != null && actionId == null)
			{
				return new MaterializationRecord(
					entryId, MaterializationRecordKind.Script, occurredAt,
					script, 0, null, null, exposeData);
			}
			if (script != null && actionId != null)
			{
				return new MaterializationRecord(
					entryId, MaterializationRecordKind.Define, occurredAt,
					null, actionId.Value, null, script, exposeData);
			}
			if (script == null && actionId != null)
			{
				return new MaterializationRecord(
					entryId, MaterializationRecordKind.Invocation, occurredAt,
					null, actionId.Value, arguments, null, exposeData);
			}

			throw new LanguageException($"Journal row id={entryId} has inconsistent Script/Action shape (both NULL).");
		}

	}

}
