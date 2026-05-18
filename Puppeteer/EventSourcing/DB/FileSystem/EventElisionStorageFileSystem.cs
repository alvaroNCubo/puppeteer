using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	// Cross-process safe elision store.
	//
	// El bug original: dos procesos con FS compartido (actor local con Cue() y
	// actor remoto con Job()) hacian read-modify-AtomicReplace sobre el mismo
	// elision.bin/skips.bin y la segunda AtomicReplace silenciosamente perdia
	// las contribuciones del primero (clasico lost-update).
	//
	// El fix: cada MarkEventsAsElided escribe un .batch unico (con guid) en
	// pending/. Workers no compiten — cada uno escribe a un archivo distinto.
	// La coordinacion ocurre solo en la consolidacion (witness lock OS-level
	// via FileShare.None), que mergea pending/*.batch al elision.bin y
	// skips.bin canonicos. El commit transaccional es el rename .tmp -> .batch.
	internal sealed class EventElisionStorageFileSystem : EventElisionStorage
	{
		// Formato del archivo consolidado elision.bin.
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'E', (byte)'L' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		private const int RECORD_SIZE = 12;

		// Formato de los .batch en pending/.
		private static readonly byte[] BATCH_MAGIC = new byte[] { (byte)'P', (byte)'B', (byte)'A', (byte)'T' };
		private const ushort BATCH_FORMAT_VERSION = 1;
		private const int BATCH_HEADER_SIZE = 22; // 4 magic + 2 version + 4 reactionId + 8 ticks + 4 entryCount

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly SkipStore skipStore;
		private readonly PendingBatchDirectory pendingDir;
		private readonly ReaderWriterLockSlim elisionLock = new ReaderWriterLockSlim();

		// Vista in-memory unificada (consolidated + batches ya mergeados).
		// Se refresca lazily desde disco cuando otros procesos publicaron cambios.
		private readonly Dictionary<int, HashSet<long>> eventsByReaction = new();
		private DateTime consolidatedMtimeUtc;
		private readonly HashSet<string> mergedBatchNames = new();

		internal EventElisionStorageFileSystem(
			IActorEventJournalClient eventJournalClient,
			string connectionString,
			string filePath,
			IAtomicFileOperation atomicOp,
			SkipStore skipStore,
			PendingBatchDirectory pendingDir)
			: base(eventJournalClient, connectionString)
		{
			if (filePath == null) throw new ArgumentNullException(nameof(filePath));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));
			if (skipStore == null) throw new ArgumentNullException(nameof(skipStore));
			if (pendingDir == null) throw new ArgumentNullException(nameof(pendingDir));

			this.filePath = filePath;
			this.atomicOp = atomicOp;
			this.skipStore = skipStore;
			this.pendingDir = pendingDir;

			LoadConsolidated();
			MergeAllPendingIntoMemory();
		}

		private void LoadConsolidated()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);

			eventsByReaction.Clear();
			mergedBatchNames.Clear();

			if (!File.Exists(filePath))
			{
				consolidatedMtimeUtc = DateTime.MinValue;
				return;
			}

			byte[] data = File.ReadAllBytes(filePath);
			consolidatedMtimeUtc = File.GetLastWriteTimeUtc(filePath);

			if (data.Length < HEADER_SIZE) return;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + RECORD_SIZE <= data.Length; i++)
			{
				int reactionId = BitConverter.ToInt32(data, offset); offset += 4;
				long entryId = BitConverter.ToInt64(data, offset); offset += 8;

				if (!eventsByReaction.ContainsKey(reactionId))
					eventsByReaction[reactionId] = new HashSet<long>();
				eventsByReaction[reactionId].Add(entryId);
			}
		}

		// Mergea TODOS los pending batches del disco al estado in-memory.
		// Llamado al inicio y por RefreshIfStale cuando aparecen archivos nuevos.
		private void MergeAllPendingIntoMemory()
		{
			foreach (var batchPath in pendingDir.ListBatches())
			{
				string name = Path.GetFileName(batchPath);
				if (mergedBatchNames.Contains(name)) continue;

				if (TryDecodeBatch(batchPath, out int rid, out _, out long[] ids))
				{
					if (!eventsByReaction.ContainsKey(rid))
						eventsByReaction[rid] = new HashSet<long>();
					foreach (var id in ids)
						eventsByReaction[rid].Add(id);
					mergedBatchNames.Add(name);
				}
			}
		}

		// Refresca la vista in-memory si el consolidado en disco cambio o si
		// aparecieron batches nuevos de otros procesos. Operacion barata
		// (mtime + dir listing) cuando no hay cambios.
		private void RefreshIfStale()
		{
			DateTime currentMtime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;

			elisionLock.EnterUpgradeableReadLock();
			try
			{
				if (currentMtime != consolidatedMtimeUtc)
				{
					elisionLock.EnterWriteLock();
					try { LoadConsolidated(); }
					finally { elisionLock.ExitWriteLock(); }
				}

				bool needsBatchMerge = false;
				foreach (var batchPath in pendingDir.ListBatches())
				{
					if (!mergedBatchNames.Contains(Path.GetFileName(batchPath))) { needsBatchMerge = true; break; }
				}

				if (needsBatchMerge)
				{
					elisionLock.EnterWriteLock();
					try { MergeAllPendingIntoMemory(); }
					finally { elisionLock.ExitWriteLock(); }
				}
			}
			finally { elisionLock.ExitUpgradeableReadLock(); }
		}

		private static byte[] EncodeBatch(int reactionId, DateTime timestamp, long[] entryIds)
		{
			byte[] data = new byte[BATCH_HEADER_SIZE + entryIds.Length * 8];
			int offset = 0;

			Buffer.BlockCopy(BATCH_MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), BATCH_FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), reactionId); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 8), timestamp.Ticks); offset += 8;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), entryIds.Length); offset += 4;

			foreach (long id in entryIds)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), id);
				offset += 8;
			}

			return data;
		}

		private static bool TryDecodeBatch(string batchPath, out int reactionId, out DateTime timestamp, out long[] entryIds)
		{
			reactionId = 0;
			timestamp = DateTime.MinValue;
			entryIds = Array.Empty<long>();

			byte[] data;
			try { data = File.ReadAllBytes(batchPath); }
			catch (IOException) { return false; }
			catch (UnauthorizedAccessException) { return false; }

			if (data.Length < BATCH_HEADER_SIZE) return false;
			if (data[0] != BATCH_MAGIC[0] || data[1] != BATCH_MAGIC[1] || data[2] != BATCH_MAGIC[2] || data[3] != BATCH_MAGIC[3])
				return false;

			int offset = 4;
			ushort version = BitConverter.ToUInt16(data, offset); offset += 2;
			if (version != BATCH_FORMAT_VERSION) return false;

			reactionId = BitConverter.ToInt32(data, offset); offset += 4;
			long ticks = BitConverter.ToInt64(data, offset); offset += 8;
			timestamp = new DateTime(ticks, DateTimeKind.Utc);
			int entryCount = BitConverter.ToInt32(data, offset); offset += 4;

			if (entryCount < 0 || offset + entryCount * 8 > data.Length) return false;

			entryIds = new long[entryCount];
			for (int i = 0; i < entryCount; i++)
			{
				entryIds[i] = BitConverter.ToInt64(data, offset);
				offset += 8;
			}
			return true;
		}

		// Persiste el estado eventsByReaction al elision.bin canonico via AtomicReplace.
		// Llamado solo desde Consolidate / RemoveElidedIds bajo elisionLock(write).
		private void SaveConsolidated()
		{
			int totalRecords = 0;
			foreach (var set in eventsByReaction.Values)
				totalRecords += set.Count;

			byte[] data = new byte[HEADER_SIZE + totalRecords * RECORD_SIZE];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), totalRecords); offset += 4;

			foreach (var kvp in eventsByReaction)
			{
				int reactionId = kvp.Key;
				foreach (long entryId in kvp.Value)
				{
					BitConverter.TryWriteBytes(data.AsSpan(offset, 4), reactionId); offset += 4;
					BitConverter.TryWriteBytes(data.AsSpan(offset, 8), entryId); offset += 8;
				}
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);

			consolidatedMtimeUtc = File.GetLastWriteTimeUtc(filePath);
		}

		// Fuerza un drain de pending/ a los archivos consolidados, usado por Distill
		// para que la pasada de fisicalizacion vea un snapshot estable. Si otro
		// proceso ya esta consolidando, esperamos a que termine releyendo del disco
		// — no necesitamos *nosotros* ser quien consolide.
		internal void ForceConsolidate()
		{
			TryConsolidate(blockUntilDone: true);
		}

		// Intenta consolidar pending/ -> elision.bin + skips.bin. Best-effort:
		// si otro proceso tiene el witness, simplemente retorna y otra ronda
		// recogera nuestro batch (los .batch que dejamos quedan durables).
		private void TryConsolidate(bool blockUntilDone)
		{
			using (var lease = pendingDir.TryAcquireWitness())
			{
				if (lease == null)
				{
					// Otro proceso consolida ahora. Si vamos a depender del resultado
					// (Distill), esperamos un poco y recargamos del disco. La carga
					// real de los .batch nuestros la haran ellos.
					if (blockUntilDone) WaitForConsolidationToProgress();
					return;
				}

				// Estamos solos. Snapshot de batches a procesar.
				List<string> batches = pendingDir.ListBatches();
				if (batches.Count == 0) return;

				// Releemos consolidated.bin del disco — no confiamos en in-memory
				// porque otros procesos pueden haber publicado cambios entre nuestra
				// ultima refresh y este momento.
				var canonical = LoadConsolidatedFromDisk();
				var skipSet = new SortedSet<long>(skipStore.Load());

				foreach (var batchPath in batches)
				{
					if (!TryDecodeBatch(batchPath, out int rid, out _, out long[] ids))
						continue;

					if (!canonical.ContainsKey(rid))
						canonical[rid] = new HashSet<long>();
					foreach (var id in ids)
					{
						canonical[rid].Add(id);
						skipSet.Add(id);
					}
				}

				// Orden de escritura: primero skips.bin, luego elision.bin, luego
				// borramos los batches consumidos. Si crasheamos en medio:
				//   - antes de skips.bin: nada cambio, batches sobreviven.
				//   - despues de skips.bin antes de elision.bin: skips tiene la
				//     info nueva, elision aun no, batches sobreviven -> proxima
				//     consolidacion los reaplica (idempotente).
				//   - despues de elision.bin antes del delete: ambos consolidados,
				//     batches sobreviven -> proxima consolidacion los reaplica
				//     (idempotente, mismo estado final).
				skipStore.Save(skipSet);

				elisionLock.EnterWriteLock();
				try
				{
					eventsByReaction.Clear();
					foreach (var kvp in canonical)
						eventsByReaction[kvp.Key] = new HashSet<long>(kvp.Value);
					SaveConsolidated();
					mergedBatchNames.Clear();
				}
				finally { elisionLock.ExitWriteLock(); }

				pendingDir.DeleteBatches(batches);
			}
		}

		private Dictionary<int, HashSet<long>> LoadConsolidatedFromDisk()
		{
			var result = new Dictionary<int, HashSet<long>>();

			atomicOp.RecoverFromIncompleteOperation(filePath);
			if (!File.Exists(filePath)) return result;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE) return result;
			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return result;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;
			for (int i = 0; i < count && offset + RECORD_SIZE <= data.Length; i++)
			{
				int reactionId = BitConverter.ToInt32(data, offset); offset += 4;
				long entryId = BitConverter.ToInt64(data, offset); offset += 8;

				if (!result.ContainsKey(reactionId))
					result[reactionId] = new HashSet<long>();
				result[reactionId].Add(entryId);
			}
			return result;
		}

		// Espera pasiva por un consolidador externo. Usado solo desde ForceConsolidate.
		// Si nuestro batch propio sigue en pending/ tras la espera, lo consolidamos
		// nosotros mismos (en la siguiente iteracion el lease ya estara libre).
		private void WaitForConsolidationToProgress()
		{
			const int maxSpins = 20;
			const int spinMs = 50;
			for (int i = 0; i < maxSpins; i++)
			{
				Thread.Sleep(spinMs);
				if (pendingDir.CountBatches() == 0) return;

				// Reintentar tomar el witness — si el otro consolidador termino,
				// quiza queden batches nuevos que escribimos durante su trabajo.
				using (var lease = pendingDir.TryAcquireWitness())
				{
					if (lease != null)
					{
						// Soltamos y dejamos que TryConsolidate(false) corra normalmente
						// — esto evita re-tomarlo dentro del using.
					}
					else continue;
				}
				TryConsolidate(blockUntilDone: false);
				return;
			}
		}

		protected internal override bool IsEventElided(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			RefreshIfStale();
			var skipSet = skipStore.Load();
			if (skipSet.Contains(dairyId)) return true;

			// Tambien chequear pending batches que aun no se mergearon al skipStore.
			// La operacion es necesaria porque skipStore.Load() solo lee skips.bin
			// consolidado, no los .batch.
			elisionLock.EnterReadLock();
			try
			{
				foreach (var set in eventsByReaction.Values)
					if (set.Contains(dairyId)) return true;
			}
			finally { elisionLock.ExitReadLock(); }

			return false;
		}

		protected internal override Task<bool> IsEventElidedAsync(long dairyId)
		{
			return Task.FromResult(IsEventElided(dairyId));
		}

		protected internal override void MarkEventsAsElided(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			if (dairyIds == null) throw new ArgumentNullException(nameof(dairyIds));
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			if (dairyIds.Length == 0) return;

			// Fase 1 (commit transaccional): escribir batch durable a pending/.
			// El rename .tmp -> .batch es el commit point — antes nada es visible,
			// despues el batch sobrevive cualquier crash.
			byte[] payload = EncodeBatch(reactionId, timestamp, dairyIds);
			pendingDir.WriteBatch(payload);

			// Fase 2 (in-memory): este proceso ve los nuevos markers inmediatamente.
			// Otros procesos los veran cuando consoliden o cuando refresquen su vista.
			elisionLock.EnterWriteLock();
			try
			{
				if (!eventsByReaction.ContainsKey(reactionId))
					eventsByReaction[reactionId] = new HashSet<long>();
				foreach (long id in dairyIds)
					eventsByReaction[reactionId].Add(id);
			}
			finally { elisionLock.ExitWriteLock(); }

			// Fase 3 (consolidacion oportunista): intentar drenar pending/. Si otro
			// proceso ya tiene el witness, no esperamos — nuestro .batch quedo
			// durable en disco y otra ronda lo recogera. Esto evita acoplar la
			// latencia del Mark a la consolidacion.
			TryConsolidate(blockUntilDone: false);
		}

		protected internal override Task MarkEventsAsElidedAsync(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			MarkEventsAsElided(dairyIds, reactionId, timestamp);
			return Task.CompletedTask;
		}

		protected internal override void GetElidedEventsByReaction(int reactionId, List<long> result)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			if (result == null) throw new ArgumentNullException(nameof(result));

			result.Clear();
			RefreshIfStale();

			elisionLock.EnterReadLock();
			try
			{
				if (eventsByReaction.TryGetValue(reactionId, out var set))
				{
					result.AddRange(set);
					result.Sort();
				}
			}
			finally { elisionLock.ExitReadLock(); }
		}

		protected internal override Task GetElidedEventsByReactionAsync(int reactionId, List<long> result)
		{
			GetElidedEventsByReaction(reactionId, result);
			return Task.CompletedTask;
		}

		protected internal override void GetElidedEventsInRange(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			if (result == null) throw new ArgumentNullException(nameof(result));
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();
			RefreshIfStale();

			var skipSet = skipStore.Load();
			foreach (long id in skipSet)
			{
				if (id >= fromDairyId && id <= toDairyId)
					result.Add(id);
			}

			// Tambien incluir lo que esta en pending y aun no llego a skips.bin.
			elisionLock.EnterReadLock();
			try
			{
				foreach (var set in eventsByReaction.Values)
				{
					foreach (long id in set)
					{
						if (id >= fromDairyId && id <= toDairyId)
							result.Add(id);
					}
				}
			}
			finally { elisionLock.ExitReadLock(); }
		}

		protected internal override Task GetElidedEventsInRangeAsync(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			GetElidedEventsInRange(fromDairyId, toDairyId, result);
			return Task.CompletedTask;
		}

		// Materialize v2 / Fase 3 — wire verb (d) DameElidedRange.
		// GAP HONESTO: el formato binario actual del consolidado (RECORD_SIZE = 12 =
		// reactionId + entryId) NO preserva timestamp por marker. Devolvemos
		// DateTime.MinValue como sentinel. Orden de marcaje se reduce a EntryId.
		// SQL backends preservan Timestamp via columna EventElision.Timestamp y
		// devuelven el valor real.
		protected internal override void ReadElisionMarkersInRange(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();
			RefreshIfStale();

			elisionLock.EnterReadLock();
			try
			{
				foreach (var kvp in eventsByReaction)
				{
					int reactionId = kvp.Key;
					foreach (long entryId in kvp.Value)
					{
						if (entryId >= fromDairyId && entryId <= toDairyId)
						{
							result.Add(new MaterializationElisionMarker(entryId, reactionId, DateTime.MinValue));
						}
					}
				}
			}
			finally { elisionLock.ExitReadLock(); }

			result.Sort((a, b) =>
			{
				int cmp = a.Timestamp.CompareTo(b.Timestamp);
				if (cmp != 0) return cmp;
				return a.EntryId.CompareTo(b.EntryId);
			});
		}

		protected internal override Task ReadElisionMarkersInRangeAsync(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ReadElisionMarkersInRange(fromDairyId, toDairyId, result);
			return Task.CompletedTask;
		}

		// Cuenta de IDs genuinamente nuevos respecto al estado in-memory actual.
		// Aproximacion: no fuerza refresh ni mira pending de otros procesos. Es
		// suficiente porque metadata.TotalNonSkippedCount se recomputa cada
		// startup desde skipStore.LoadCount() (DiaryStorageFileSystem.cs:101-108).
		internal int GetNewSkipCount(long[] dairyIds)
		{
			if (dairyIds == null) throw new ArgumentNullException(nameof(dairyIds));

			RefreshIfStale();
			var skipSet = skipStore.Load();

			elisionLock.EnterReadLock();
			try
			{
				int count = 0;
				foreach (long id in dairyIds)
				{
					if (skipSet.Contains(id)) continue;
					bool inMemory = false;
					foreach (var set in eventsByReaction.Values)
					{
						if (set.Contains(id)) { inMemory = true; break; }
					}
					if (!inMemory) count++;
				}
				return count;
			}
			finally { elisionLock.ExitReadLock(); }
		}

		// Usado por Distill: tras materializar fisicamente las elisiones, los EntryIds
		// removidos dejan de ser "logicamente elididos" para volverse "ausentes". Caller
		// es DiaryStorageFileSystem.Distill, que ya forzo ForceConsolidate antes para
		// que la operacion vea un snapshot estable.
		internal void RemoveElidedIds(IEnumerable<long> dairyIds)
		{
			if (dairyIds == null) throw new ArgumentNullException(nameof(dairyIds));

			elisionLock.EnterWriteLock();
			try
			{
				foreach (var id in dairyIds)
				{
					foreach (var set in eventsByReaction.Values)
					{
						set.Remove(id);
					}
				}
				SaveConsolidated();
			}
			finally { elisionLock.ExitWriteLock(); }
		}

		// Test seam: cantidad de batches pending. Usado por tests de concurrencia
		// para verificar que la consolidacion drena el directorio.
		internal int CountPendingBatches() => pendingDir.CountBatches();
	}
}
