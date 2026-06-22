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
					HandleMissingFile(indexEntry, client);
					continue;
				}

				lastProcessedEntryId = ReadSingleFile(filePath, afterEntryId, eventDataPool, client, canContinue, lastProcessedEntryId, includeExposeData);
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
							// Broken integrity: rehydration abandons the rest of the file
							// by contract (best-effort recovery), but the event is serious. It goes to
							// IPuppeteerLogger.Error so it is visible on stderr (ConsoleLogger
							// default) or on the sink the host injected via Performance.Logger(...).
							client.Logger.Error(
								$"[REHYDRATION] CRC mismatch in file {filePath} at position {fs.Position - recordLen}. Skipping rest of file.",
								new System.IO.InvalidDataException("CRC mismatch detected"));
							break;
						}

						long entryId = BinaryEventCodec.PeekEntryId(recordBody);
						if (entryId <= afterEntryId) continue;
						if (skipSet.Contains(entryId)) continue;

						bool ok = BinaryEventCodec.TryDecode(recordBody, recordLen,
							out var eventType, out _, out var occurredAt,
							out var content, out var actionId,
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
								out _, out _,
								out int defineActionId, out string defineStatementText, out _,
								compression, encryption, encryptionKey);

							if (okDefine)
							{
								client.AddKnownActionFromDefine(defineActionId, defineStatementText);
								lastProcessedEntryId = entryId;
								continue;
							}

							client.Logger.Error(
								$"[REHYDRATION] Failed to decode record with entryId {entryId} in file {filePath}. Continuing.",
								new System.IO.InvalidDataException("Record decode failed (not Script, not Define)"));
							continue;
						}

						if (eventType == EventRecordType.Script)
						{
							var scriptData = eventDataPool.RentScript();
							scriptData.EntryId = entryId;
							scriptData.OccurredAt = occurredAt;
							scriptData.Script = content;
							scriptData.ExposeData = includeExposeData ? exposeDataDecoded : null;

							client.ReplayEvent(scriptData);
						}
						else
						{
							if (!client.IsActionKnown(actionId))
							{
								// Replay without context: the actionId is not in the actor's
								// action cache. Phase 5 guarantees Define-precedes-Invocation by
								// construction, so this should ONLY fire with pre-Phase-5
								// journals or corruption. Visibility via the host logger.
								client.Logger.Error(
									$"[REHYDRATION] Action {actionId} not known for entryId {entryId}. Skipping.",
									new System.IO.InvalidDataException($"Orphan Invocation: actionId={actionId} entryId={entryId}"));
								continue;
							}

							var actionData = eventDataPool.RentAction();
							actionData.EntryId = entryId;
							actionData.OccurredAt = occurredAt;
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

		private void HandleMissingFile(IndexEntry indexEntry, IActorEventJournalClient client)
		{
			long firstId = indexEntry.FirstEntryId;
			long lastId = indexEntry.LastEntryId;

			if (firstId <= 0 || lastId <= 0 || lastId < firstId)
			{
				client.Logger.Error(
					$"[REHYDRATION] Missing journal file for sequence {indexEntry.FileNumber}. Index entry has invalid range [{firstId}..{lastId}]. Continuing rehydration.",
					new System.IO.InvalidDataException($"Index entry range invalid: [{firstId}..{lastId}]"));
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
				client.Logger.Error(
					$"[REHYDRATION] Missing journal file for sequence {indexEntry.FileNumber} (entryIds [{firstId}..{lastId}]). " +
					$"INCONSISTENCY DETECTED: {missingFromSkips.Count} entryIds NOT found in skip set. Sample: [{sampleIds}]{extra}. " +
					$"Continuing rehydration -- priority is to bring system online.",
					new System.IO.FileNotFoundException($"Missing journal file for sequence {indexEntry.FileNumber}", $"sequence-{indexEntry.FileNumber}"));
			}
			else
			{
				// Expected case: file purged correctly, all its entryIds are
				// in the skipSet. Not a problem — just information for debug.
				client.Logger.Debug(
					$"[REHYDRATION] Missing journal file for sequence {indexEntry.FileNumber} (entryIds [{firstId}..{lastId}]). " +
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
