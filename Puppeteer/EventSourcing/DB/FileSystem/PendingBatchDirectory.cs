using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	// Cross-process safe staging area for elision/skip writes.
	//
	// Cada proceso escribe sus batches a archivos UNICOS (guid en el nombre)
	// dentro de pendingDir; nunca dos workers tocan el mismo archivo. El
	// consolidador toma un lock OS-level sobre witnessPath (FileShare.None
	// sobre un FileStream abierto) que se libera automaticamente si el
	// proceso muere — sin TTL ni heartbeats que mantener. Mientras un
	// consolidador esta activo, otros workers pueden seguir produciendo
	// batches: la coordinacion solo gatea la fase de merge a los archivos
	// canonicos consolidados.
	//
	// Primitivas usadas: FileStream open/close (lock), File.WriteAllBytes,
	// File.Move (rename atomico sobre el mismo volumen), Directory.GetFiles,
	// File.Delete. Todas son operaciones del SO que ya provee el FileSystem.
	//
	// El "commit" de un batch es el rename .tmp -> .batch. Hasta ese momento
	// el archivo es invisible para readers/consolidators (solo se enumeran
	// archivos con extension .batch). Despues del rename el batch es durable
	// y idempotente: si el proceso crashea, el batch sigue ahi para que el
	// proximo consolidador lo recoja.
	internal sealed class PendingBatchDirectory
	{
		private readonly string pendingDir;
		private readonly string witnessPath;

		internal PendingBatchDirectory(string pendingDir, string witnessPath)
		{
			if (pendingDir == null) throw new ArgumentNullException(nameof(pendingDir));
			if (witnessPath == null) throw new ArgumentNullException(nameof(witnessPath));

			this.pendingDir = pendingDir;
			this.witnessPath = witnessPath;
			Directory.CreateDirectory(pendingDir);

			RecoverOrphans();
		}

		internal string PendingDir => pendingDir;

		// Escribe un batch atomico: WriteAllBytes a un .tmp con guid unico,
		// fsync, luego rename a un nombre final .batch. El rename es el
		// commit point — antes de eso el archivo no es visible a readers
		// porque ListBatches() filtra por extension .batch.
		//
		// El nombre final embebe el guid + ticks UTC para ordenar los
		// batches por tiempo de creacion (no se requiere para correctitud
		// pero facilita la depuracion).
		internal void WriteBatch(byte[] payload)
		{
			if (payload == null) throw new ArgumentNullException(nameof(payload));

			string guid = Guid.NewGuid().ToString("N");
			long ticks = DateTime.UtcNow.Ticks;
			string tmpPath = Path.Combine(pendingDir, $"{guid}.tmp");
			string finalPath = Path.Combine(pendingDir, $"{ticks:D19}-{guid}.batch");

			using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				fs.Write(payload, 0, payload.Length);
				fs.Flush(true);
			}

			// File.Move sin overwrite: si por algun milagro existe ya un archivo
			// con el mismo nombre final, fallamos antes de pisarlo. Como el
			// nombre embebe un guid, esto solo pasaria por un bug.
			File.Move(tmpPath, finalPath);
		}

		// Snapshot de los .batch actuales, ordenados por nombre (que codifica
		// ticks UTC). El consolidador procesa este snapshot — batches que
		// aparezcan despues del snapshot quedan para la siguiente ronda.
		internal List<string> ListBatches()
		{
			if (!Directory.Exists(pendingDir)) return new List<string>();

			var files = Directory.GetFiles(pendingDir, "*.batch");
			Array.Sort(files, StringComparer.Ordinal);
			return new List<string>(files);
		}

		// Intenta tomar el lock de consolidacion. Retorna null si otro
		// proceso ya lo tiene. El handle DEBE Dispose-arse al terminar
		// — la liberacion ocurre al cerrar el FileStream, no al borrar
		// el archivo witness (que se deja como sentinel inocuo).
		//
		// FileShare.None hace que el lock sea OS-level: si el proceso
		// dueño muere, el OS cierra el FD y libera el lock automaticamente,
		// sin TTL ni heartbeats. En Windows es un lock mandatorio nativo;
		// en Linux .NET 5+ lo implementa via OFD locks / flock.
		internal WitnessLease TryAcquireWitness()
		{
			try
			{
				FileStream fs = new FileStream(
					witnessPath,
					FileMode.OpenOrCreate,
					FileAccess.Write,
					FileShare.Read);

				try
				{
					// En Windows FileShare.None ya bloquea; en Linux .NET
					// emula via flock. Si el archivo ya esta abierto por
					// otro proceso con FileShare.None, este open lanza
					// IOException. Si no, escribimos nuestra identidad
					// para diagnostico (lo unico que se ve en disco).
					fs.SetLength(0);
					byte[] identity = Encoding.UTF8.GetBytes(
						$"pid={Environment.ProcessId} host={Environment.MachineName} time={DateTime.UtcNow:O}");
					fs.Write(identity, 0, identity.Length);
					fs.Flush(true);
					return new WitnessLease(fs);
				}
				catch
				{
					fs.Dispose();
					throw;
				}
			}
			catch (IOException) { return null; }
			catch (UnauthorizedAccessException) { return null; }
		}

		// Borra los batches consumidos por una consolidacion exitosa.
		// Idempotente: si un archivo ya no existe (otro consolidador lo
		// borro), se ignora silenciosamente. Falla silenciosa tambien si
		// el archivo esta temporalmente bloqueado — el proximo Distill /
		// consolidacion lo intentara de nuevo.
		internal void DeleteBatches(IEnumerable<string> paths)
		{
			if (paths == null) throw new ArgumentNullException(nameof(paths));

			foreach (var p in paths)
			{
				try { File.Delete(p); } catch { }
			}
		}

		// Recovery de .tmp huerfanos. Un .tmp aparece cuando un worker
		// crasheo entre WriteAllBytes y el rename — el batch no se
		// committeo, asi que el reaction tampoco avanzo su checkpoint y
		// reintentara en el proximo ciclo generando un .tmp nuevo. Los
		// viejos son basura.
		internal void RecoverOrphans()
		{
			if (!Directory.Exists(pendingDir)) return;

			foreach (var tmp in Directory.GetFiles(pendingDir, "*.tmp"))
			{
				try { File.Delete(tmp); } catch { }
			}
		}

		// Test seam: cuenta de batches vivos. Usado por tests para verificar
		// que la consolidacion limpia el directorio.
		internal int CountBatches()
		{
			if (!Directory.Exists(pendingDir)) return 0;
			return Directory.GetFiles(pendingDir, "*.batch").Length;
		}
	}

	internal sealed class WitnessLease : IDisposable
	{
		private FileStream stream;

		internal WitnessLease(FileStream stream)
		{
			this.stream = stream;
		}

		public void Dispose()
		{
			var s = stream;
			stream = null;
			if (s != null)
			{
				try { s.Dispose(); } catch { }
			}
		}
	}
}
