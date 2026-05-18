using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class JournalReader
	{
		private const int READ_BUFFER_SIZE = 262144;

		private readonly string journalDir;
		private readonly SparseIndex index;
		private readonly HashSet<long> skipSet;
		private readonly PayloadCompression compression;
		private readonly EncryptionMode encryption;
		private readonly byte[] encryptionKey;

		internal JournalReader(string journalDir, SparseIndex index, HashSet<long> skipSet,
			PayloadCompression compression = PayloadCompression.None,
			EncryptionMode encryption = EncryptionMode.None,
			byte[] encryptionKey = null)
		{
			if (journalDir == null) throw new ArgumentNullException(nameof(journalDir));
			if (index == null) throw new ArgumentNullException(nameof(index));

			this.journalDir = journalDir;
			this.index = index;
			this.skipSet = skipSet ?? new HashSet<long>();
			this.compression = compression;
			this.encryption = encryption;
			this.encryptionKey = encryptionKey;
		}

		internal long ReadAll(long afterEntryId, EventDataPool eventDataPool,
			IActorEventJournalClient client, Func<bool> canContinue, bool includeExposeData = false)
		{
			if (eventDataPool == null) throw new ArgumentNullException(nameof(eventDataPool));
			if (client == null) throw new ArgumentNullException(nameof(client));

			long lastProcessedEntryId = afterEntryId;
			var allEntries = index.GetAllEntries();
			if (allEntries.Count == 0) return lastProcessedEntryId;

			int startIdx = FindStartingIndexEntry(allEntries, afterEntryId);

			for (int i = startIdx; i < allEntries.Count; i++)
			{
				if (canContinue != null && !canContinue()) break;

				var indexEntry = allEntries[i];
				string filePath = GetJournalFilePath(indexEntry.FileNumber);

				if (!File.Exists(filePath))
				{
					HandleMissingFile(indexEntry);
					continue;
				}

				lastProcessedEntryId = ReadSingleFile(filePath, afterEntryId, eventDataPool, client, canContinue, lastProcessedEntryId, includeExposeData);
			}

			return lastProcessedEntryId;
		}

		internal long ReadAllBackward(long afterEntryId, EventDataPool eventDataPool,
			IActorEventJournalClient client, Func<bool> canContinue, bool includeExposeData = false)
		{
			if (eventDataPool == null) throw new ArgumentNullException(nameof(eventDataPool));
			if (client == null) throw new ArgumentNullException(nameof(client));

			long lastProcessedEntryId = afterEntryId;
			var allEntries = index.GetAllEntries();
			if (allEntries.Count == 0) return lastProcessedEntryId;

			// Read files in reverse order; within each file read all records then emit in reverse
			for (int i = allEntries.Count - 1; i >= 0; i--)
			{
				if (canContinue != null && !canContinue()) break;

				var indexEntry = allEntries[i];

				// Skip files whose entire range is before afterEntryId
				if (afterEntryId > 0 && indexEntry.LastEntryId <= afterEntryId) continue;

				string filePath = GetJournalFilePath(indexEntry.FileNumber);
				if (!File.Exists(filePath))
				{
					HandleMissingFile(indexEntry);
					continue;
				}

				lastProcessedEntryId = ReadSingleFileBackward(filePath, afterEntryId, eventDataPool, client, canContinue, lastProcessedEntryId, includeExposeData);
			}

			return lastProcessedEntryId;
		}

		private long ReadSingleFileBackward(string filePath, long afterEntryId, EventDataPool eventDataPool,
			IActorEventJournalClient client, Func<bool> canContinue, long lastProcessedEntryId, bool includeExposeData = false)
		{
			// Read all valid records from the file into a list, then emit in reverse order
			var records = new System.Collections.Generic.List<(long entryId, EventRecordType eventType, DateTime occurredAt, string ip, string user, string content, int actionId, string exposeData)>();

			using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, READ_BUFFER_SIZE, FileOptions.SequentialScan))
			{
				if (fs.Length < JournalWriter.HEADER_SIZE) return lastProcessedEntryId;
				fs.Seek(JournalWriter.HEADER_SIZE, SeekOrigin.Begin);

				while (fs.Position < fs.Length)
				{
					byte[] lenBuf = new byte[4];
					int read = fs.Read(lenBuf, 0, 4);
					if (read < 4) break;

					int recordLen = BitConverter.ToInt32(lenBuf, 0);
					if (recordLen <= 0 || fs.Position + recordLen > fs.Length) break;

					byte[] recordBody = System.Buffers.ArrayPool<byte>.Shared.Rent(recordLen);
					try
					{
						read = fs.Read(recordBody, 0, recordLen);
						if (read < recordLen) break;

						if (!BinaryEventCodec.ValidateCrc(recordBody, recordLen)) break;

						long entryId = BinaryEventCodec.PeekEntryId(recordBody);
						if (entryId <= afterEntryId) continue;
						if (skipSet.Contains(entryId)) continue;

						bool ok = BinaryEventCodec.TryDecode(recordBody, recordLen,
							out var eventType, out _, out var occurredAt,
							out var ip, out var user, out var content, out var actionId,
							out var exposeDataDecoded,
							compression, encryption, encryptionKey);

						if (ok)
						{
							records.Add((entryId, eventType, occurredAt, ip, user, content, actionId, includeExposeData ? exposeDataDecoded : null));
						}
						else
						{
							// Phase 4 of the Action refactor: legacy TryDecode returns false on
							// Define records. Attempt the Define-specific decode and capture
							// the canonical sentence into `content` so the replay loop can
							// populate the actionCommands cache via AddKnownActionFromDefine.
							bool okDefine = BinaryEventCodec.TryDecodeDefine(recordBody, recordLen,
								out _, out var defineOccurredAt,
								out var defineIp, out var defineUser,
								out var defineActionId, out var defineStatementText, out var defineExpose,
								compression, encryption, encryptionKey);

							if (okDefine)
							{
								records.Add((entryId, EventRecordType.Define, defineOccurredAt, defineIp, defineUser, defineStatementText, defineActionId, includeExposeData ? defineExpose : null));
							}
						}
					}
					finally
					{
						System.Buffers.ArrayPool<byte>.Shared.Return(recordBody);
					}
				}
			}

			for (int i = records.Count - 1; i >= 0; i--)
			{
				if (canContinue != null && !canContinue()) break;

				var (entryId, eventType, occurredAt, ip, user, content, actionId, exposeJson) = records[i];

				if (eventType == EventRecordType.Script)
				{
					var scriptData = eventDataPool.RentScript();
					scriptData.EntryId = entryId;
					scriptData.OccurredAt = occurredAt;
					scriptData.Ip = ip;
					scriptData.User = user;
					scriptData.Script = content;
					scriptData.ExposeData = exposeJson;
					client.ReplayEvent(scriptData);
				}
				else if (eventType == EventRecordType.Define)
				{
					// Phase 4 of the Action refactor: replay a Define record by
					// populating the actionCommands cache with the canonical sentence.
					// No EventData is dispatched for Define rows — the actor's
					// vocabulary mutates, but no domain side effect runs.
					client.AddKnownActionFromDefine(actionId, content);
				}
				else
				{
					if (!client.IsActionKnown(actionId)) continue;
					var actionData = eventDataPool.RentAction();
					actionData.EntryId = entryId;
					actionData.OccurredAt = occurredAt;
					actionData.Ip = ip;
					actionData.User = user;
					actionData.ActionId = actionId;
					actionData.Arguments = content;
					actionData.ExposeData = exposeJson;
					client.ReplayEvent(actionData);
				}

				lastProcessedEntryId = entryId;
			}

			return lastProcessedEntryId;
		}

		internal long CountNonSkippedEvents(long afterEntryId)
		{
			long count = 0;
			var allEntries = index.GetAllEntries();
			if (allEntries.Count == 0) return 0;

			int startIdx = FindStartingIndexEntry(allEntries, afterEntryId);

			for (int i = startIdx; i < allEntries.Count; i++)
			{
				var indexEntry = allEntries[i];
				string filePath = GetJournalFilePath(indexEntry.FileNumber);

				if (!File.Exists(filePath))
					continue;

				count += CountEventsInFile(filePath, afterEntryId);
			}

			return count;
		}

		private long ReadSingleFile(string filePath, long afterEntryId, EventDataPool eventDataPool,
			IActorEventJournalClient client, Func<bool> canContinue, long lastProcessedEntryId, bool includeExposeData = false)
		{
			using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, READ_BUFFER_SIZE, FileOptions.SequentialScan))
			{
				if (fs.Length < JournalWriter.HEADER_SIZE) return lastProcessedEntryId;
				fs.Seek(JournalWriter.HEADER_SIZE, SeekOrigin.Begin);

				while (fs.Position < fs.Length)
				{
					if (canContinue != null && !canContinue()) break;

					byte[] lenBuf = new byte[4];
					int read = fs.Read(lenBuf, 0, 4);
					if (read < 4) break;

					int recordLen = BitConverter.ToInt32(lenBuf, 0);
					if (recordLen <= 0 || fs.Position + recordLen > fs.Length) break;

					byte[] recordBody = ArrayPool<byte>.Shared.Rent(recordLen);
					try
					{
						read = fs.Read(recordBody, 0, recordLen);
						if (read < recordLen) break;

						if (!BinaryEventCodec.ValidateCrc(recordBody, recordLen))
						{
							Console.WriteLine($"[REHYDRATION WARNING] CRC mismatch in file {filePath} at position {fs.Position - recordLen}. Skipping rest of file.");
							break;
						}

						long entryId = BinaryEventCodec.PeekEntryId(recordBody);
						if (entryId <= afterEntryId) continue;
						if (skipSet.Contains(entryId)) continue;

						bool ok = BinaryEventCodec.TryDecode(recordBody, recordLen,
							out var eventType, out _, out var occurredAt,
							out var ip, out var user, out var content, out var actionId,
							out var exposeDataDecoded,
							compression, encryption, encryptionKey);

						if (!ok)
						{
							// Phase 4 of the Action refactor: legacy TryDecode returns false
							// on Define records. Attempt the Define-specific decode and, if
							// it succeeds, populate the actionCommands cache with the
							// canonical sentence (no EventData dispatch — Define is
							// vocabulary, not effect).
							bool okDefine = BinaryEventCodec.TryDecodeDefine(recordBody, recordLen,
								out _, out _, out _, out _,
								out int defineActionId, out string defineStatementText, out _,
								compression, encryption, encryptionKey);

							if (okDefine)
							{
								client.AddKnownActionFromDefine(defineActionId, defineStatementText);
								lastProcessedEntryId = entryId;
								continue;
							}

							Console.WriteLine($"[REHYDRATION WARNING] Failed to decode record with entryId {entryId} in file {filePath}. Continuing.");
							continue;
						}

						if (eventType == EventRecordType.Script)
						{
							var scriptData = eventDataPool.RentScript();
							scriptData.EntryId = entryId;
							scriptData.OccurredAt = occurredAt;
							scriptData.Ip = ip;
							scriptData.User = user;
							scriptData.Script = content;
							scriptData.ExposeData = includeExposeData ? exposeDataDecoded : null;

							client.ReplayEvent(scriptData);
						}
						else
						{
							if (!client.IsActionKnown(actionId))
							{
								Console.WriteLine($"[REHYDRATION WARNING] Action {actionId} not known for entryId {entryId}. Skipping.");
								continue;
							}

							var actionData = eventDataPool.RentAction();
							actionData.EntryId = entryId;
							actionData.OccurredAt = occurredAt;
							actionData.Ip = ip;
							actionData.User = user;
							actionData.ActionId = actionId;
							actionData.Arguments = content;
							actionData.ExposeData = includeExposeData ? exposeDataDecoded : null;

							client.ReplayEvent(actionData);
						}

						lastProcessedEntryId = entryId;
					}
					finally
					{
						ArrayPool<byte>.Shared.Return(recordBody);
					}
				}
			}

			return lastProcessedEntryId;
		}

		private long CountEventsInFile(string filePath, long afterEntryId)
		{
			long count = 0;

			using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, READ_BUFFER_SIZE, FileOptions.SequentialScan))
			{
				if (fs.Length < JournalWriter.HEADER_SIZE) return 0;
				fs.Seek(JournalWriter.HEADER_SIZE, SeekOrigin.Begin);

				while (fs.Position < fs.Length)
				{
					byte[] lenBuf = new byte[4];
					int read = fs.Read(lenBuf, 0, 4);
					if (read < 4) break;

					int recordLen = BitConverter.ToInt32(lenBuf, 0);
					if (recordLen <= 0 || fs.Position + recordLen > fs.Length) break;

					byte[] peek = new byte[9];
					read = fs.Read(peek, 0, Math.Min(9, recordLen));
					if (read < 9) break;

					long entryId = BinaryEventCodec.PeekEntryId(peek);

					long remaining = recordLen - read;
					if (remaining > 0) fs.Seek(remaining, SeekOrigin.Current);

					if (entryId <= afterEntryId) continue;
					if (skipSet.Contains(entryId)) continue;

					count++;
				}
			}

			return count;
		}

		private void HandleMissingFile(IndexEntry indexEntry)
		{
			long firstId = indexEntry.FirstEntryId;
			long lastId = indexEntry.LastEntryId;

			if (firstId <= 0 || lastId <= 0 || lastId < firstId)
			{
				Console.WriteLine($"[REHYDRATION ERROR] Missing journal file for sequence {indexEntry.FileNumber}. Index entry has invalid range [{firstId}..{lastId}]. Continuing rehydration.");
				return;
			}

			List<long> missingFromSkips = new();
			for (long id = firstId; id <= lastId; id++)
			{
				if (!skipSet.Contains(id))
				{
					missingFromSkips.Add(id);
					if (missingFromSkips.Count > 100) break;
				}
			}

			if (missingFromSkips.Count > 0)
			{
				string sampleIds = string.Join(", ", missingFromSkips.Count <= 10 ? missingFromSkips : missingFromSkips.GetRange(0, 10));
				string extra = missingFromSkips.Count > 10 ? $" (and {missingFromSkips.Count - 10}+ more)" : "";
				Console.WriteLine($"[REHYDRATION ERROR] Missing journal file for sequence {indexEntry.FileNumber} (entryIds [{firstId}..{lastId}]). " +
					$"INCONSISTENCY DETECTED: {missingFromSkips.Count} entryIds NOT found in skip set. Sample: [{sampleIds}]{extra}. " +
					$"Continuing rehydration -- priority is to bring system online.");
			}
			else
			{
				Console.WriteLine($"[REHYDRATION INFO] Missing journal file for sequence {indexEntry.FileNumber} (entryIds [{firstId}..{lastId}]). " +
					$"All entryIds verified as skipped. File was correctly purged.");
			}
		}

		private int FindStartingIndexEntry(IReadOnlyList<IndexEntry> allEntries, long afterEntryId)
		{
			if (afterEntryId <= 0) return 0;

			int lo = 0, hi = allEntries.Count - 1;
			int result = 0;

			while (lo <= hi)
			{
				int mid = lo + (hi - lo) / 2;
				if (allEntries[mid].FirstEntryId <= afterEntryId)
				{
					result = mid;
					lo = mid + 1;
				}
				else
				{
					hi = mid - 1;
				}
			}

			return result;
		}

		private string GetJournalFilePath(int sequence)
		{
			return Path.Combine(journalDir, $"journal_{sequence:D6}.bin");
		}
	}
}
