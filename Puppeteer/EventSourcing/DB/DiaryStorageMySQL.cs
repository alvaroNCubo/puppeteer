using MySql.Data.MySqlClient;
using Puppeteer;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class DiaryStorageMySQL : DiaryStorage
	{
		private readonly string mySqlWriteScriptCommand;
		private readonly string mySqlWriteDefineCommand;
		private readonly string mySqlWriteInvocationCommand;
		private readonly string mySqlWriteScriptCommandWithExposeData;
		private readonly string mySqlWriteDefineCommandWithExposeData;
		private readonly string mySqlWriteInvocationCommandWithExposeData;
		// Phase 6 of the Action refactor: dropped mySqlWriteActionCommand,
		// mySqlWriteNewActionCommand, and their *WithExposeData variants. The
		// post-refactor write API is WriteScriptEntry / WriteDefineEntry /
		// WriteInvocationEntry / WriteDefineWithFirstInvocation.

		// Requires AllowMultipleStatements = true in the MySQL connection string: "Server=...;Database=...;AllowMultipleStatements=true;..."
		internal DiaryStorageMySQL(IActorEventJournalClient eventJournalClient, string connectionString) : base(eventJournalClient, connectionString)
		{
			mySqlWriteScriptCommand = $"insert into `{Name}` (id, OccurredAt, Script) values (@id, @OccurredAt, @Script)";

			// Define rows: script = canonical sentence, action = actionId,
			// arguments = NULL. The first invocation lives in a separate
			// Invocation row.
			mySqlWriteDefineCommand = $"INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@EntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL)";

			// Invocation rows: script = NULL, action = actionId, arguments = args.
			mySqlWriteInvocationCommand = $"INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@EntryId, @OccurredAt, NULL, @ActionID, @Arguments)";

			mySqlWriteScriptCommandWithExposeData = $@"
				BEGIN;
				{mySqlWriteScriptCommand};
				INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@DiaryId, @ExposeJson);
				COMMIT;";

			mySqlWriteDefineCommandWithExposeData = $@"
				BEGIN;
				{mySqlWriteDefineCommand};
				INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@DiaryId, @ExposeJson);
				COMMIT;";

			mySqlWriteInvocationCommandWithExposeData = $@"
				BEGIN;
				{mySqlWriteInvocationCommand};
				INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@DiaryId, @ExposeJson);
				COMMIT;";

			eventElisionStorage = new EventElisionStorageMySQL(eventJournalClient, connectionString);
			eventMaterializationStorage = new EventMaterializationStorageMySQL(eventJournalClient, connectionString);
			materializationCheckpointStorage = new MaterializationCheckpointStorageMySQL(eventJournalClient, connectionString);
		}

		private async Task<bool> CreateDiaryAsync(String nombreDelDiario)
		{
			StringBuilder statement = new StringBuilder();
			bool created = false;

			statement
				.Append("SELECT count(TABLE_NAME) alreadyexists FROM information_schema.TABLES WHERE TABLE_NAME = ")
				.Append('\'').Append(nombreDelDiario).Append('\'')
				.Append(" AND TABLE_SCHEMA in (SELECT DATABASE());");

			statement
				.Append("create table IF NOT EXISTS `").Append(nombreDelDiario).Append('`')
				.Append('(')
				.Append("	id BIGINT NOT NULL,")
				.Append("	OccurredAt DATETIME(3) NOT NULL,")
				.Append("	Script TEXT NULL,")
				.Append("	Action INT NULL,")
				.Append("	Arguments TEXT NULL,")
				.Append("	Skip TINYINT(1) NOT NULL DEFAULT 0,")
				.Append("	PRIMARY KEY (id)")
				.Append(") ENGINE=InnoDB CHARSET=utf8;");

			// Phase 6 of the Action refactor: dropped CREATE TABLE _ACTION.
			// Action definitions live in the journal as Define records.

			statement.Append(@"
				create table IF NOT EXISTS Follower
				(
					FollowerId INT NOT NULL,
					EntryId BIGINT NOT NULL,
					Description VARCHAR(45) NULL,
					PRIMARY KEY (FollowerId)
				) ENGINE=InnoDB CHARSET=utf8;
			");

			statement.Append(@"
				CREATE TABLE IF NOT EXISTS Reaction (
					Id INT NOT NULL,
					Reaction TEXT NOT NULL,
					PRIMARY KEY (Id)
				) ENGINE=InnoDB CHARSET=utf8;"
			);

			statement
				.Append(@"CREATE TABLE IF NOT EXISTS ReactionCheckpoint (
					ReactionId INT NOT NULL,
					Pattern INT NOT NULL,
					DiaryId BIGINT NOT NULL DEFAULT 0,
					ConfirmedDiaryId BIGINT NOT NULL DEFAULT 0,
					PRIMARY KEY (ReactionId, Pattern)
				) ENGINE=InnoDB CHARSET=utf8;");

			// Resume optimization (checkpoint redesign, step 2): two global cursors per
			// reaction (read-front + closed-frontier) for coverage resume.
			statement
				.Append(@"CREATE TABLE IF NOT EXISTS ReactionFrontier (
					ReactionId INT NOT NULL,
					HighWater BIGINT NOT NULL DEFAULT 0,
					ClosedFrontier BIGINT NOT NULL DEFAULT 0,
					PRIMARY KEY (ReactionId)
				) ENGINE=InnoDB CHARSET=utf8;");

			// ExposeData: optional lateral table (one row per event that executed expose).
			// The follower path forces includeExposeData=true and its replay SELECT
			// performs LEFT JOIN ExposeData, so the table must always exist — just like
			// Follower/Reaction/ReactionCheckpoint/ReactionFrontier. Reported by a follower deployment
			// 2.0.1-beta.9817 (follower on a fresh MySQL DB where the manual script never
			// ran). The column is DiaryId (not DairyId): it is the one the engine reads in the JOIN
			// and in the INSERT INTO ExposeData (DiaryId, ExposeJson).
			statement
				.Append(@"CREATE TABLE IF NOT EXISTS ExposeData (
					DiaryId BIGINT NOT NULL,
					ExposeJson TEXT NOT NULL,
					PRIMARY KEY (DiaryId)
				) ENGINE=InnoDB CHARSET=utf8;");

			string sql = statement.ToString();
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				await connection.OpenAsync();
				using (MySqlCommand command = new MySqlCommand(sql, connection))
				using (DbDataReader reader = await command.ExecuteReaderAsync())
				{
					await reader.ReadAsync();
					created = reader.GetInt32(0) == 0;

					await reader.CloseAsync();
				}
				await connection.CloseAsync();
			}

			return created;
		}

		private bool CreateDiary(String nombreDelDiario)
		{
			StringBuilder statement = new StringBuilder();
			bool created = false;

			statement
				.Append("SELECT count(TABLE_NAME) alreadyexists FROM information_schema.TABLES WHERE TABLE_NAME = ")
				.Append('\'').Append(nombreDelDiario).Append('\'')
				.Append(" AND TABLE_SCHEMA in (SELECT DATABASE());");

			statement
				.Append("create table IF NOT EXISTS `").Append(nombreDelDiario).Append('`')
				.Append('(')
				.Append("	id BIGINT NOT NULL,")
				.Append("	OccurredAt DATETIME(3) NOT NULL,")
				.Append("	Script TEXT NULL,")
				.Append("	Action INT NULL,")
				.Append("	Arguments TEXT NULL,")
				.Append("	Skip TINYINT(1) NOT NULL DEFAULT 0,")
				.Append("	PRIMARY KEY (id)")
				.Append(")	ENGINE=InnoDB CHARSET=utf8;");

			// Schema validation: if the actor's table already existed with an
			// old schema (pre-rename), CREATE TABLE IF NOT EXISTS is a
			// no-op and the next SELECT with d.OccurredAt / d.Action /
			// d.Arguments throws MySqlException 'Unknown column ...' that the
			// RehydrateFromEvent catch silenced with Console.WriteLine,
			// leaving the actor with CurrentEntryId=0 and an apparently
			// empty journal. Here we
			// inspect the real columns and throw immediately with the
			// actionable ALTER TABLE.

			// Phase 6 of the Action refactor: dropped CREATE TABLE _ACTION.
			// Action definitions live in the journal as Define records.

			statement.Append(@"
				create table IF NOT EXISTS Follower
				(
					FollowerId INT NOT NULL,
					EntryId BIGINT NOT NULL,
					Description VARCHAR(45) NULL,
					PRIMARY KEY (FollowerId)
				) ENGINE=InnoDB CHARSET=utf8;
			");

			statement.Append(@"
				CREATE TABLE IF NOT EXISTS Reaction (
					Id INT NOT NULL,
					Reaction TEXT NOT NULL,
					PRIMARY KEY (Id)
				) ENGINE=InnoDB CHARSET=utf8;"
			);

			statement.Append(@"
				CREATE TABLE IF NOT EXISTS ReactionCheckpoint (
					ReactionId INT NOT NULL,
					Pattern INT NOT NULL,
					DiaryId BIGINT NOT NULL DEFAULT 0,
					ConfirmedDiaryId BIGINT NOT NULL DEFAULT 0,
					PRIMARY KEY (ReactionId, Pattern)
				) ENGINE=InnoDB CHARSET=utf8;"
			);

			// Resume optimization (checkpoint redesign, step 2): two global cursors per reaction.
			statement.Append(@"
				CREATE TABLE IF NOT EXISTS ReactionFrontier (
					ReactionId INT NOT NULL,
					HighWater BIGINT NOT NULL DEFAULT 0,
					ClosedFrontier BIGINT NOT NULL DEFAULT 0,
					PRIMARY KEY (ReactionId)
				) ENGINE=InnoDB CHARSET=utf8;"
			);

			// ExposeData: optional lateral table (one row per event that executed expose).
			// The follower path forces includeExposeData=true and its replay SELECT
			// performs LEFT JOIN ExposeData, so the table must always exist — just like
			// Follower/Reaction/ReactionCheckpoint/ReactionFrontier. Reported by a follower deployment
			// 2.0.1-beta.9817 (follower on a fresh MySQL DB where the manual script never
			// ran). The column is DiaryId (not DairyId): it is the one the engine reads in the JOIN
			// and in the INSERT INTO ExposeData (DiaryId, ExposeJson).
			statement.Append(@"
				CREATE TABLE IF NOT EXISTS ExposeData (
					DiaryId BIGINT NOT NULL,
					ExposeJson TEXT NOT NULL,
					PRIMARY KEY (DiaryId)
				) ENGINE=InnoDB CHARSET=utf8;"
			);

			string sql = statement.ToString();
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					using (DbDataReader reader = command.ExecuteReader())
					{
						reader.Read();
						created = reader.GetInt32(0) == 0;

						reader.Close();
					}
				}
				finally
				{
					connection.Close();
				}
			}

			if (!created)
			{
				ValidateExistingSchemaOrThrow(nombreDelDiario);
			}

			return created;
		}

		// Inspects `information_schema.COLUMNS` to detect pre-existing actor
		// tables with the older legacy schema (FechaHora,
		// NOT NULL Ip/User/Script, without Action/Arguments). If a mismatch is found,
		// throw LanguageException with the ALTER TABLE script ready to run.
		// Called only when created==false (the table already existed).
		private void ValidateExistingSchemaOrThrow(string tableName)
		{
			var columns = new Dictionary<string, (string DataType, string IsNullable)>(StringComparer.OrdinalIgnoreCase);
			string sql = $@"
				SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
				FROM information_schema.COLUMNS
				WHERE TABLE_NAME = '{tableName}'
				  AND TABLE_SCHEMA = (SELECT DATABASE())";

			using (var connection = new MySqlConnection(ConnectionString))
			{
				connection.Open();
				using (var command = new MySqlCommand(sql, connection))
				using (var reader = command.ExecuteReader())
				{
					while (reader.Read())
					{
						columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
					}
				}
			}

			var issues = new List<string>();
			var alters = new List<string>();

			if (columns.ContainsKey("FechaHora") && !columns.ContainsKey("OccurredAt"))
			{
				issues.Add("column 'FechaHora' should be renamed to 'OccurredAt'");
				alters.Add($"ALTER TABLE `{tableName}` CHANGE COLUMN `FechaHora` `OccurredAt` DATETIME(3) NOT NULL;");
			}
			if (!columns.ContainsKey("Action"))
			{
				issues.Add("column 'Action' is missing");
				alters.Add($"ALTER TABLE `{tableName}` ADD COLUMN `Action` INT NULL AFTER `Script`;");
			}
			if (!columns.ContainsKey("Arguments"))
			{
				issues.Add("column 'Arguments' is missing");
				alters.Add($"ALTER TABLE `{tableName}` ADD COLUMN `Arguments` TEXT NULL AFTER `Action`;");
			}
			if (columns.TryGetValue("Script", out var scriptCol) && scriptCol.IsNullable.Equals("NO", StringComparison.OrdinalIgnoreCase))
			{
				issues.Add("column 'Script' must allow NULL");
				alters.Add($"ALTER TABLE `{tableName}` MODIFY COLUMN `Script` TEXT NULL;");
			}

			if (issues.Count == 0) return;

			var msg = new StringBuilder();
			msg.Append("MySQL table `").Append(tableName).Append("` has a schema from an older Puppeteer version and is not compatible with the current package. ");
			msg.Append("Detected issues: ").AppendJoin("; ", issues).Append(". ");
			msg.Append("Run the following migration on the actor's database before starting:\n");
			foreach (var alter in alters) msg.Append("  ").Append(alter).Append('\n');
			msg.Append("After running these statements (and the equivalent rename `DairyId`→`EntryId` on the `Follower` table if present), restart the consumer.");

			throw new LanguageException(msg.ToString());
		}

		// Phase 6 of the Action refactor: dropped LoadSingleAction +
		// LoadActionsWithExitStatus (sync + async). The lateral _ACTION table is
		// gone; Define entries in the journal populate the cache via
		// AddKnownActionFromDefine.


		protected internal override long RehydrateFromEvent(long afterEntryId = 0, bool includeExposeData = false)
		{
			EventJournalClient.IsNew = CreateDiary(this.Name);

			bool canContinueReplay = false;
			long ultimoId = afterEntryId;
			long delta = 0;
			bool salir = false;
			int cantidadDeLeaderInitialization = 0;

			// Forward replay: events in ascending id order.
			string orderByClause = "ORDER BY d.id ASC";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					// Materialize v2 / Phase 0.5: the journal's Skip column is authoritative
					// for rehydration. Replaces LEFT JOIN EventElision with WHERE d.Skip = 0.
					// EventElisionStorageMySQL.MarkEventsAsElided sets Skip = 1 transactionally.
					string sqlCount = $@"
						SELECT Count(*) Cantidad
						FROM `{base.Name}` d
						WHERE d.Skip = 0 AND d.id > {ultimoId}";
					using (MySqlCommand command = new MySqlCommand(sqlCount, connection))
					using (DbDataReader reader = command.ExecuteReader())
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
				int fails = 0;
				long entryId = 0;

				while (!salir && (canContinueReplay = EventJournalClient.CanContinueReplay(entryId)))
				{
					// Phase 5 of the Action refactor: legacy LoadActionsWithExitStatus
					// (pre-load the _ACTION lateral table) is dropped. Define entries
					// in the journal populate the cache via AddKnownActionFromDefine
					// in entry-id order — Define always precedes Invocation by
					// construction (atomic write + monotonic ordering).

					string sql = includeExposeData
						? $@"SELECT d.Id, d.OccurredAt, d.Script, d.Action, d.Arguments, ed.ExposeJson
							FROM `{base.Name}` d
							LEFT JOIN ExposeData ed ON d.id = ed.DiaryId
							WHERE d.Skip = 0 AND d.id > {ultimoId}
							{orderByClause}"
						: $@"SELECT d.Id, d.OccurredAt, d.Script, d.Action, d.Arguments
							FROM `{base.Name}` d
							WHERE d.Skip = 0 AND d.id > {ultimoId}
							{orderByClause}";

					using (MySqlConnection connection = new MySqlConnection(ConnectionString))
					{
						try
						{
							connection.Open();

							using (MySqlCommand command = new MySqlCommand(sql, connection))
							using (DbDataReader reader = command.ExecuteReader())
							{
								try
								{
									bool salirRead = false;
									int intentos = 5;
									while (!salirRead && intentos > 0 && (canContinueReplay = EventJournalClient.CanContinueReplay(entryId)))
									{
										try
										{
											while (reader.Read() && (canContinueReplay = EventJournalClient.CanContinueReplay(entryId)))
											{

												fails = 0;

												entryId = reader.GetInt64(0);

												DateTime occurredAt = reader.GetDateTime(1);

												bool scriptIsNull = reader.IsDBNull(2);
												bool actionIsNull = reader.IsDBNull(3);

												// Phase 4 of the Action refactor: process Define rows.
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

													// Phase 5: legacy LoadSingleAction recovery lookup
													// dropped — Define entries in the journal populate
													// the cache by construction.

													string arguments = reader.GetString(4);

													string exposeJson = includeExposeData && !reader.IsDBNull(5) ? reader.GetString(5) : null;

												var actionData = base.EventDataPool.RentAction();
													actionData.EntryId = entryId;
													actionData.OccurredAt = occurredAt;
													actionData.ActionId = actionId;
													actionData.Arguments = arguments;
													actionData.ExposeData = exposeJson;

													EventJournalClient.ReplayEvent(actionData);
												}
												else
												{
													string script = reader.GetString(2);
													string exposeJson = includeExposeData && !reader.IsDBNull(5) ? reader.GetString(5) : null;

													var scriptData = base.EventDataPool.RentScript();
													scriptData.EntryId = entryId;
													scriptData.OccurredAt = occurredAt;
													scriptData.Script = script;
													scriptData.ExposeData = exposeJson;

													EventJournalClient.ReplayEvent(scriptData);
												}
											}
											salirRead = true;
										}
										catch (MySqlException mysqlException)
										{
											intentos--;
											fails++;
											// Goes through IPuppeteerLogger.Error instead of Console.WriteLine: with
											// the default ConsoleLogger it goes to stderr (Console.Error) with prefix
											// [Puppeteer ERROR], and hosts that injected Serilog/MEL/NLog
											// via Performance.Logger(...) capture it too. Before the
											// log4net->IPuppeteerLogger refactor this was invisible on stderr.
											Logger.Error($"RehydrateFromEvent retry {fails} on actor '{base.Name}'. type:{mysqlException.GetType()} error:{mysqlException.Message}", mysqlException);
										}
									}
								}
								catch (Exception e)
								{
									Logger.Error($"RehydrateFromEvent inner block failure on actor '{base.Name}'. type:{e.GetType()} error:{e.Message}", e);
								}
								finally
								{
									reader.Close();
								}
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
			catch (Exception e)
			{
				// Goes through IPuppeteerLogger.Error: with the default ConsoleLogger it goes to
				// Console.Error (stderr) with prefix [Puppeteer ERROR]. If the host
				// injected a logger (Serilog/MEL/NLog) via Performance.Logger(...),
				// it also receives it there. The swallow-and-continue is kept to
				// allow transient failures not to abort the actor — the caller
				// (EventSourcingStorage) sees ultimoId = afterEntryId and continues with
				// partial state; the log is the only evidence of the failure.
				Logger.Error($"RehydrateFromEvent outer block failure on actor '{base.Name}'. type:{e.GetType()} error:{e.Message}", e);
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
			long result = 0;
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					using (MySqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							result = reader.GetInt64("DiaryId");
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

				using (MySqlConnection connection = new MySqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (MySqlCommand command = new MySqlCommand(sql, connection))
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

		protected internal override async Task WriteScriptEntryAsync(long entryId, string script, DateTime now, string exposeData)
		{
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();

					string sql;
					bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);

					if (hasExposeData)
					{
						sql = mySqlWriteScriptCommandWithExposeData;
					}
					else
					{
						sql = mySqlWriteScriptCommand;
					}

					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@id", entryId);
						command.Parameters.AddWithValue("@OccurredAt", now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
						command.Parameters.AddWithValue("@Script", script);

						if (hasExposeData)
						{
							command.Parameters.AddWithValue("@DiaryId", entryId);
							command.Parameters.AddWithValue("@ExposeJson", exposeData);
						}

						await command.ExecuteNonQueryAsync();
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{mySqlWriteScriptCommand} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception("Error al escribir en MySQL el Script en el Diary: [" + mySqlWriteScriptCommand + "]. " + e.Message);
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{mySqlWriteScriptCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					await connection.CloseAsync();
				}
			}

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeScriptRecord(entryId, script, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// Phase 6 of the Action refactor: dropped WriteActionEntryAsync +
		// WriteNewActionEntryAsync overrides. Use WriteInvocationEntryAsync /
		// WriteDefineEntryAsync / WriteDefineWithFirstInvocationAsync.

		protected internal override void WriteScriptEntry(long entryId, string script, DateTime now, string exposeData = null)
		{
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					string sql;
					bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);

					if (hasExposeData)
					{
						sql = mySqlWriteScriptCommandWithExposeData;
					}
					else
					{
						sql = mySqlWriteScriptCommand;
					}

					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@id", entryId);
						command.Parameters.AddWithValue("@OccurredAt", now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
						command.Parameters.AddWithValue("@Script", script);

						if (hasExposeData)
						{
							command.Parameters.AddWithValue("@DiaryId", entryId);
							command.Parameters.AddWithValue("@ExposeJson", exposeData);
						}

						command.ExecuteNonQuery();
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{mySqlWriteScriptCommand} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception("Error al escribir en MySQL el Script en el Diary: [" + mySqlWriteScriptCommand + "]. " + e.Message);
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{mySqlWriteScriptCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					connection.Close();
				}
			}

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeScriptRecord(entryId, script, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// Phase 6 of the Action refactor: dropped WriteActionEntry and
		// WriteNewActionEntry overrides. The post-cutover write API is
		// WriteScriptEntry / WriteDefineEntry / WriteInvocationEntry /
		// WriteDefineWithFirstInvocation.

		// Phase 3 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// new write APIs for the post-cutover path. WriteDefineEntry inserts a row
		// in the journal with `script` populated by the canonical Define sentence
		// and `action` populated by actionId — the post-cutover discriminator
		// (script != NULL ∧ action != NULL → Define). NO INSERT into the lateral
		// _ACTION table — that is the legacy WriteNewActionEntry's job, and
		// Phase 6 drops the table entirely. Replay silently skips Define rows in
		// Phase 3 (see the discriminator update in RehydrateFromEvent).
		protected internal override void WriteDefineEntry(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
					string sql = hasExposeData ? mySqlWriteDefineCommandWithExposeData : mySqlWriteDefineCommand;

					using (MySqlCommand command = new MySqlCommand(sql, connection))
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
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{mySqlWriteDefineCommand} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception("Error al escribir Define entry en MySQL: [" + mySqlWriteDefineCommand + "]. " + e.Message);
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{mySqlWriteDefineCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					connection.Close();
				}
			}

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeDefineRecord(actionId, defineStatementText, entryId, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		protected internal override async Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();

					bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
					string sql = hasExposeData ? mySqlWriteDefineCommandWithExposeData : mySqlWriteDefineCommand;

					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

						await command.ExecuteNonQueryAsync();
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{mySqlWriteDefineCommand} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception("Error al escribir Define entry async en MySQL: [" + mySqlWriteDefineCommand + "]. " + e.Message);
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{mySqlWriteDefineCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					await connection.CloseAsync();
				}
			}

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeDefineRecord(actionId, defineStatementText, entryId, now, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// WriteInvocationEntry inserts an invocation row in the journal:
		// script = NULL, action = actionId, arguments = args. Phase 6 of the
		// Action refactor dropped the legacy WriteActionEntry — this is the
		// only path for invocations post-cutover.
		protected internal override void WriteInvocationEntry(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(arguments);

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
					string sql = hasExposeData ? mySqlWriteInvocationCommandWithExposeData : mySqlWriteInvocationCommand;

					using (MySqlCommand command = new MySqlCommand(sql, connection))
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
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{mySqlWriteInvocationCommand} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception("Error al escribir Invocation entry en MySQL: [" + mySqlWriteInvocationCommand + "]. " + e.Message);
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{mySqlWriteInvocationCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					connection.Close();
				}
			}

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeInvocationRecord(actionId, entryId, now, arguments, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		protected internal override async Task WriteInvocationEntryAsync(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(arguments);

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();

					bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
					string sql = hasExposeData ? mySqlWriteInvocationCommandWithExposeData : mySqlWriteInvocationCommand;

					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

						await command.ExecuteNonQueryAsync();
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{mySqlWriteInvocationCommand} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception("Error al escribir Invocation entry async en MySQL: [" + mySqlWriteInvocationCommand + "]. " + e.Message);
				}
				catch (Exception e)
				{
					Logger.Error($@"sql:{mySqlWriteInvocationCommand} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					await connection.CloseAsync();
				}
			}

			if (OnRecordWritten != null)
			{
				byte[] record = EncodeInvocationRecord(actionId, entryId, now, arguments, exposeData);
				OnRecordWritten.Invoke(entryId, record);
			}
		}

		// Phase 4 atomic write — see DiaryStorage.cs for the contract. Wraps the
		// two INSERTs in an explicit BEGIN/COMMIT pair so the pair is transactional
		// — either both rows land in the journal or neither does.
		protected internal override void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);
			ArgumentNullException.ThrowIfNull(arguments);

			bool hasExposeData = !string.IsNullOrWhiteSpace(exposeData);
			string sql = hasExposeData
				? $@"
					BEGIN;
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);
					INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@InvocationEntryId, @ExposeJson);
					COMMIT;"
				: $@"
					BEGIN;
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);
					COMMIT;";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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
				catch (MySqlException e)
				{
					Logger.Error($@"WriteDefineWithFirstInvocation actionId:{actionId} defineEntryId:{defineEntryId} invocationEntryId:{invocationEntryId} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception($"Error al escribir Define+Invocation atomic en MySQL (actionId={actionId}). {e.Message}");
				}
				catch (Exception e)
				{
					Logger.Error($@"WriteDefineWithFirstInvocation actionId:{actionId} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					connection.Close();
				}
			}

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
					BEGIN;
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);
					INSERT INTO ExposeData (DiaryId, ExposeJson) VALUES (@InvocationEntryId, @ExposeJson);
					COMMIT;"
				: $@"
					BEGIN;
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@DefineEntryId, @OccurredAt, @DefineStatementText, @ActionID, NULL);
					INSERT INTO `{Name}` (id, occurredAt, script, action, arguments) VALUES (@InvocationEntryId, @OccurredAt, NULL, @ActionID, @Arguments);
					COMMIT;";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

						await command.ExecuteNonQueryAsync();
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($@"WriteDefineWithFirstInvocationAsync actionId:{actionId} defineEntryId:{defineEntryId} invocationEntryId:{invocationEntryId} type:{e.GetType()} error:{e.Message}", e);
					throw new Exception($"Error al escribir Define+Invocation atomic async en MySQL (actionId={actionId}). {e.Message}");
				}
				catch (Exception e)
				{
					Logger.Error($@"WriteDefineWithFirstInvocationAsync actionId:{actionId} type:{e.GetType()} error:{e.Message}", e);
					throw;
				}
				finally
				{
					await connection.CloseAsync();
				}
			}

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
			Debug.WriteLine("Mysql tables doesn't need any change.");
		}

		protected internal override MemoryStream Archive(DateTime fechaInicio, DateTime fechaFin)
		{
			IEnumerable<string> actorsNames = ListActorNames(Name);
			if (actorsNames == null) return null;

			//ClearZipStream();
			MemoryStream compressedFileForArchive = new MemoryStream();
			ZipArchive archive = new ZipArchive(compressedFileForArchive, ZipArchiveMode.Create, false);

			StringBuilder insertString = new StringBuilder();

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				insertString.Append("USE ");
				insertString.Append(connection.Database);
				insertString.AppendLine(";");

				try
				{
					connection.Open();
					foreach (var aName in actorsNames)
					{
						msDairyPeriodRangeToExport = new MemoryStream();
						swDairyPeriodRangeToExport = new StreamWriter(msDairyPeriodRangeToExport, Encoding.UTF8);


						var fileName = aName + "-" + fechaFin.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_bak.sql";
						string sql = $"SELECT OccurredAt, Script, Skip, Id FROM `{aName}` WHERE OccurredAt >= '{fechaInicio.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}' AND OccurredAt < '{fechaFin.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}' AND Skip = 1 ORDER BY id";
						using (MySqlCommand command = new MySqlCommand(sql, connection))
						using (MySqlDataReader reader = command.ExecuteReader())
						{
							if (!reader.HasRows) continue;

							command.CommandTimeout = 60;
							while (reader.Read())
							{
								DateTime occurredAt = reader.GetDateTime(0);
								string script = reader.GetString(1);

								byte skip = Convert.ToByte(reader.GetBoolean(2));
								int id = reader.GetInt32(3);

								insertString.Append("INSERT INTO ");
								insertString.Append(aName);
								insertString.Append("(id, OccurredAt, Script, Skip) ");
								insertString.Append("VALUES (");
								insertString.Append(id);
								insertString.Append(',');
								insertString.Append("STR_TO_DATE('"); insertString.Append(occurredAt); insertString.Append("','%m/%d/%Y %h:%i:%s %p')");   //STR_TO_DATE('4/23/2019 9:37:16 AM','%m/%d/%Y %h:%i:%s %p')
								insertString.Append(',');
								insertString.Append("'" + script.Replace("'", "\\\'").Replace("\"", "\\\"") + "'");
								insertString.Append(',');
								insertString.Append(skip);
								insertString.AppendLine(");");

								swDairyPeriodRangeToExport.Write(insertString);
								swDairyPeriodRangeToExport.Flush();

								insertString.Clear();
							}
							reader.Close();

							if (msDairyPeriodRangeToExport != null) SaveTempFileToZip(archive, fileName);
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

			var existActor = ProbarSiEsTablaNueva(name);
			if (existActor && name != "general")
			{
				actors.Add(name);
			}
			else if (ProbarSiEsTablaNueva("general"))
			{
				using (MySqlConnection connection = new MySqlConnection(ConnectionString))
				{
					StringBuilder sql = new StringBuilder();

					sql.Append("SELECT TABLE_NAME FROM ");
					sql.Append(connection.Database);
					sql.Append(".INFORMATION_SCHEMA.TABLES WHERE (TABLE_NAME LIKE 'C%' AND TABLE_NAME NOT LIKE 'C%[_]%')");
					try
					{
						connection.Open();
						using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection))
						using (MySqlDataReader reader = command.ExecuteReader())
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

		private bool ProbarSiEsTablaNueva(string tableName)
		{
			bool esTablaNueva = true;
			string sql = $"SELECT 1 FROM `{tableName}` LIMIT 1";
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						var dataReader = command.ExecuteReader();
						dataReader.Close();
					}
				}
				catch
				{
					esTablaNueva = false;
				}
				finally
				{
					connection.Close();
				}
			}

			return esTablaNueva;
		}

		protected internal override void Trim(DateTime trimmedDown)
		{
			IEnumerable<string> actorsNames = ListActorNames(Name);

			StringBuilder sql = new StringBuilder();
			const string POSTFIX = "_$OLD";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					foreach (var aName in actorsNames)
					{
						bool needsTrim = NeedsTrim(aName, trimmedDown);

						if (needsTrim)
						{
							/*BUILD THE RENAME SCRIPT*/
							sql.Append("RENAME TABLE ");
							sql.Append(aName);
							sql.Append(" TO ");
							sql.Append(aName);
							sql.Append(POSTFIX);
							sql.Append(';');

							using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection))
							{
								command.ExecuteNonQuery();
							}

							sql.Clear();

							CreateDiary(aName);

							/*BUILD THE CREATE TABLE, INDEXES AND DATA SCRIPT*/
							sql.Append("INSERT INTO ");
							sql.Append('`');
							sql.Append(aName);
							sql.Append('`');
							sql.Append("(id, OccurredAt, Script, Skip) ");
							sql.Append(" SELECT id, OccurredAt, Script, Skip FROM ");
							sql.Append('`');
							sql.Append(aName);
							sql.Append(POSTFIX);
							sql.Append('`');
							sql.Append(" WHERE (Skip = 0");
							sql.Append(" AND OccurredAt < '");
							sql.Append(trimmedDown.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
							sql.Append("') OR OccurredAt >= '");
							sql.Append(trimmedDown.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
							sql.Append("' ORDER BY id;");

							/*BUILD THE DROP TABLE SCRIPT*/
							sql.AppendLine();
							sql.Append("DROP TABLE ");
							sql.Append(aName);
							sql.Append(POSTFIX);
							sql.Append(';');

							using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection))
							{
								command.CommandTimeout = 200;
								command.CommandType = CommandType.Text;
								command.ExecuteNonQuery();
							}
						}
					}
				}
				catch (MySqlException e)
				{
					Logger.Error($@"sql:{sql} type:{e.GetType()} error:{e.Message}", e);

					throw new Exception("Error al escribir en MySQL el Script en el Diary: [" + sql + "]. " + e.Message);
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

			string sql = $"SELECT * FROM `{aName}` WHERE Skip = 1 AND OccurredAt > '{trimmedDown.ToString("yyyy-M-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}' LIMIT 1;";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql.ToString(), connection))
					using (MySqlDataReader reader = command.ExecuteReader())
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
			if (minimumContributionPercent < 0 && minimumContributionPercent > 100) throw new ArgumentNullException(nameof(minimumContributionPercent));

			List<int> acumuladoPorDia = new List<int>();
			List<string> result = new List<string>(); ;


			string statement = "SELECT DATEDIFF(CURDATE(), OccurredAt) As Dias, COUNT(*) As CuentasPorDia FROM accountsLRU GROUP BY DATEDIFF(CURDATE(), OccurredAt) ORDER BY Dias ASC;";
			using (MySqlConnection connection = new MySqlConnection(connectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(statement, connection))
					using (MySqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							var cuentasPorDia = reader.GetInt32(1);
							acumuladoPorDia.Add(cuentasPorDia);
						}
						reader.Close();
					}

					int topDeCuentasACargar = CalcularMaximoDeActoresACargar(acumuladoPorDia, minimumContributionPercent);

					statement = $"SELECT Cuenta, OccurredAt, DATEDIFF(CURDATE(), OccurredAt) As Dias FROM accountsLRU ORDER BY Dias Asc LIMIT {topDeCuentasACargar};";

					using (MySqlCommand command = new MySqlCommand(statement, connection))
					using (MySqlDataReader reader = command.ExecuteReader())
					{
						while (reader.Read())
						{
							result.Add(reader.GetString(0));
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

		protected internal override long GetOrCreateReactionId(string formattedReaction)
		{
			ArgumentNullException.ThrowIfNull(formattedReaction);

			// First try to get the ID if it already exists
			string selectSql = "SELECT Id FROM Reaction WHERE Reaction = @FormattedReaction";
			long existingId = 0;

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();

					using (MySqlCommand command = new MySqlCommand(selectSql, connection))
					{
						command.Parameters.AddWithValue("@FormattedReaction", formattedReaction);
						using (MySqlDataReader reader = command.ExecuteReader())
						{
							if (reader.Read())
							{
								existingId = reader.GetInt32("Id");
							}
							reader.Close();
						}
					}

					if (existingId != 0)
					{
						return existingId;
					}

					// Does not exist: generate a new ID and insert.
					// Get the current maximum ReactionId.
					string maxIdSql = "SELECT COALESCE(MAX(Id), 0) FROM Reaction";
					int newId = 0;
					using (MySqlCommand command = new MySqlCommand(maxIdSql, connection))
					{
						object result = command.ExecuteScalar();
						newId = Convert.ToInt32(result) + 1;
					}

					// Insert the new reaction
					string insertSql = "INSERT INTO Reaction (Id, Reaction) VALUES (@ReactionId, @FormattedReaction)";
					using (MySqlCommand command = new MySqlCommand(insertSql, connection))
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

		// PHASE 5A: two-phase checkpoint - returns tuple (detected, confirmed) in a single access
		protected internal override (long detected, long confirmed) GetReactionCheckpoint(long reactionId, int seekLevel)
		{
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");
			if (seekLevel < 0) throw new LanguageException($"SeekLevel '{seekLevel}' must be zero or greater");

			string sql = "SELECT IFNULL(DiaryId, 0), IFNULL(ConfirmedDiaryId, 0) FROM ReactionCheckpoint WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
			long detected = 0;
			long confirmed = 0;

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@ReactionId", reactionId);
						command.Parameters.AddWithValue("@Pattern", seekLevel);
						using (MySqlDataReader reader = command.ExecuteReader())
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

		// Resume optimization (step 2): two global cursors per reaction (read-front +
		// closed-frontier). MySQL is a "local journal" backend (Job/Cue row of the matrix) ->
		// resume by re-reading [closed, high-water]; does not use a snapshot.
		protected internal override (long highWater, long closedFrontier) GetReactionFrontier(long reactionId)
		{
			if (reactionId <= 0) throw new LanguageException("Reaction Id must be upper than zero");

			string sql = "SELECT IFNULL(HighWater, 0), IFNULL(ClosedFrontier, 0) FROM ReactionFrontier WHERE ReactionId = @ReactionId";
			long highWater = 0;
			long closedFrontier = 0;

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

			string sql = @"INSERT INTO ReactionFrontier (ReactionId, HighWater, ClosedFrontier)
				VALUES (@ReactionId, @HighWater, @ClosedFrontier)
				ON DUPLICATE KEY UPDATE HighWater = @HighWater, ClosedFrontier = @ClosedFrontier";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
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

			var (detected, currentConfirmed) = GetReactionCheckpoint(reactionId, seekLevel);

			if (entryId > currentConfirmed)
			{
				string sql;
				if (detected != 0)
				{
					sql = "UPDATE ReactionCheckpoint SET ConfirmedDiaryId = @ConfirmedDiaryId WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
				}
				else
				{
					sql = "INSERT INTO ReactionCheckpoint (ReactionId, Pattern, DiaryId, ConfirmedDiaryId) VALUES (@ReactionId, @Pattern, @ConfirmedDiaryId, @ConfirmedDiaryId)";
				}

				using (MySqlConnection connection = new MySqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (MySqlCommand command = new MySqlCommand(sql, connection))
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

		// DEPRECATED: Kept for compatibility, returns Detected
		protected internal override long GetReactionLastProcessedEntryId(long reactionId, int pattern)
		{
			var (detected, _) = GetReactionCheckpoint(reactionId, pattern);
			return detected; // Return only Detected for compatibility
		}

		// DEPRECATED: Kept for compatibility, saves both (detected = confirmed = lastEntryId)
		protected internal override void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long lastEntryId)
		{
			if (pattern < 0) throw new LanguageException("Pattern must be zero or upper");
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

				using (MySqlConnection connection = new MySqlConnection(ConnectionString))
				{
					try
					{
						connection.Open();
						using (MySqlCommand command = new MySqlCommand(sql, connection))
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

		protected internal override bool MarkEventsAsElidedWithCheckpoint(Follower.CheckpointCommit commit)
		{
			ArgumentNullException.ThrowIfNull(commit);

			long reactionId = commit.ReactionId;
			long[] eventIds = commit.EventIds;
			DateTime timestamp = commit.Timestamp;
			Follower.CheckpointVector newCheckpoint = commit.CheckpointVector;

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				connection.Open();
				using (MySqlTransaction transaction = connection.BeginTransaction())
				{
					try
					{
						// Lexicographic comparison: allow matches that share events.
						bool isGreater = false;
						for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
						{
							long newEntryId = newCheckpoint.Get(seekLevel);

							string checkSql = "SELECT IFNULL(DiaryId, 0) FROM ReactionCheckpoint WHERE ReactionId = @ReactionId AND Pattern = @Pattern";
							long currentEntryId = 0;

							using (MySqlCommand checkCmd = new MySqlCommand(checkSql, connection, transaction))
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
								INSERT IGNORE INTO EventElision (DiaryId, ReactionId, Timestamp)
								VALUES (@DiaryId, @ReactionId, @Timestamp)";

							using (MySqlCommand insertCmd = new MySqlCommand(insertElisionSql, connection, transaction))
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
								INSERT INTO ReactionCheckpoint (ReactionId, Pattern, DiaryId, ConfirmedDiaryId)
								VALUES (@ReactionId, @Pattern, @DiaryId, 0)
								ON DUPLICATE KEY UPDATE DiaryId = @DiaryId";

							using (MySqlCommand upsertCmd = new MySqlCommand(upsertCheckpointSql, connection, transaction))
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

		// Stage 5 of the Distill refactor. Replaces the throw NotImplementedException
		// inherited from the base. Physically materializes the elisions accumulated in
		// EventElision: deletes the rows of `{Name}` whose ids are in EventElision
		// (together with their entries in EventElision), except the record with MAX(id) —
		// invariant "the last record is never physically elided".
		//
		// Semantics: single transaction. If it fails, full rollback.
		// ActorHandler's outer rwLock already serializes with WriteScriptEntry /
		// MarkEventsAsElidedWithCheckpoint, so the SET of IDs to delete cannot
		// change while we run.
		//
		// EventElision serves a single actor — the "one actor per database" principle
		// (see memory project_actor_per_db_principle.md) guarantees that all the rows
		// of the table belong to this actor. The INNER JOIN with `{Name}` is defensive
		// (avoids affecting rows without a corresponding journal entry) not necessary for
		// cross-actor isolation.
		protected internal override void Distill()
		{
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				connection.Open();
				using (MySqlTransaction transaction = connection.BeginTransaction())
				{
					try
					{
						// Snapshot of the max id (preserved for the invariant).
						long maxId;
						string maxIdSql = $"SELECT COALESCE(MAX(id), 0) FROM `{Name}`";
						using (MySqlCommand cmd = new MySqlCommand(maxIdSql, connection, transaction))
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

						// Capture IDs to remove in a temporary table. It has three advantages:
						//  - Allows the DELETE on `{Name}` without the MySQL restriction "can't
						//    delete from table referenced in subquery".
						//  - The set is computed only once (consistency between the two DELETEs).
						//  - Bounds to IDs present in OUR journal (defensive against possible
						//    DiaryIds colliding with other actors in the global table).
						using (MySqlCommand cmd = new MySqlCommand(
							$@"CREATE TEMPORARY TABLE _distill_ids_{Name} (id BIGINT PRIMARY KEY) ENGINE=Memory;
							INSERT INTO _distill_ids_{Name}
							SELECT j.id FROM `{Name}` j
							INNER JOIN EventElision e ON j.id = e.DiaryId
							WHERE j.id <> @MaxId;", connection, transaction))
						{
							cmd.Parameters.AddWithValue("@MaxId", maxId);
							cmd.ExecuteNonQuery();
						}

						using (MySqlCommand cmd = new MySqlCommand(
							$@"DELETE FROM EventElision
							WHERE DiaryId IN (SELECT id FROM _distill_ids_{Name});

							DELETE FROM `{Name}`
							WHERE id IN (SELECT id FROM _distill_ids_{Name});

							DROP TEMPORARY TABLE _distill_ids_{Name};", connection, transaction))
						{
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

		// Paper 5 / Materialize v2 — Phase 2. Read raw records (without filtering Skip).
		// Layer 1 of the wire (records alone, without elision markers or checkpoints —
		// those come in Phase 3 via (c)+(d)). Distinction by column: Script != NULL
		// AND Action IS NULL = Script entry; Script != NULL AND Action != NULL = Define;
		// Script IS NULL AND Action != NULL = Invocation.
		protected internal override void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();

			string sql = $@"
				SELECT d.id, d.OccurredAt, d.Script, d.Action, d.Arguments, ed.ExposeJson
				FROM `{Name}` d
				LEFT JOIN ExposeData ed ON d.id = ed.DiaryId
				WHERE d.id > @afterId
				ORDER BY d.id ASC";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@afterId", afterEntryId);
						using (MySqlDataReader reader = command.ExecuteReader())
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
				FROM `{Name}` d
				LEFT JOIN ExposeData ed ON d.id = ed.DiaryId
				WHERE d.id > @afterId
				ORDER BY d.id ASC";

			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					await connection.OpenAsync();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					{
						command.Parameters.AddWithValue("@afterId", afterEntryId);
						using (MySqlDataReader reader = (MySqlDataReader)await command.ExecuteReaderAsync())
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
		// Atomic snapshot of the reactions registry from the Reaction table (global
		// schema per DB: Id INT PK, Reaction TEXT). One row per (formattedReaction).
		protected internal override void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			string sql = "SELECT Id, Reaction FROM Reaction ORDER BY Id";
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					using (MySqlDataReader reader = command.ExecuteReader())
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

		// Atomic snapshot of checkpoints. ReactionCheckpoint table (ReactionId,
		// Pattern, DiaryId, ConfirmedDiaryId). 'DiaryId' is the Detected field
		// (legacy naming); 'ConfirmedDiaryId' is Confirmed.
		protected internal override void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			string sql = "SELECT ReactionId, Pattern, DiaryId, ConfirmedDiaryId FROM ReactionCheckpoint ORDER BY ReactionId, Pattern";
			using (MySqlConnection connection = new MySqlConnection(ConnectionString))
			{
				try
				{
					connection.Open();
					using (MySqlCommand command = new MySqlCommand(sql, connection))
					using (MySqlDataReader reader = command.ExecuteReader())
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

		private static MaterializationRecord MapRowToRecord(MySqlDataReader reader)
		{
			long entryId = reader.GetInt64(0);
			DateTime occurredAt = reader.GetDateTime(1);
			string script = reader.IsDBNull(2) ? null : reader.GetString(2);
			int? actionId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
			string arguments = reader.IsDBNull(4) ? null : reader.GetString(4);
			string exposeData = reader.IsDBNull(5) ? null : reader.GetString(5);

			// Discriminator per Phase 6 of the Action refactor:
			//   script != NULL ∧ action IS NULL  → Script entry
			//   script != NULL ∧ action != NULL  → Define entry
			//   script IS NULL ∧ action != NULL  → Invocation entry
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
