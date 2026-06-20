using System;
using System.IO;

namespace Puppeteer.EventSourcing.DB
{
	// Validacion eager de paths declarados en la configuracion de almacenamiento.
	// Se ejecuta al construir el Diary, NO al procesar el primer perform command,
	// para que una mala configuracion (path inexistente, sin permisos, read-only)
	// falle ruidosamente lo antes posible con un mensaje accionable. Aplica al
	// `localBufferPath` independientemente del dbType del backend canonico, y al
	// `path=` del FileSystem. No valida conexiones remotas a MySQL/SQLServer:
	// ellas fallan organicamente al inicio del rehidratar.
	internal static class StoragePathValidator
	{
		internal static void EnsureLocalBufferPathIsUsable(string localBufferPath)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(localBufferPath);
			EnsureWritableDirectory(localBufferPath, StorageConnectionString.LocalBufferPathKey);
		}

		internal static void EnsureFileSystemPathIsUsable(string fileSystemPath)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(fileSystemPath);
			EnsureWritableDirectory(fileSystemPath, "path");
		}

		internal static void EnsureBufferAndCanonicalAreDistinct(string canonicalPath, string localBufferPath)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(canonicalPath);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(localBufferPath);

			string canonicalAbs = NormalizeForComparison(canonicalPath);
			string bufferAbs = NormalizeForComparison(localBufferPath);

			if (string.Equals(canonicalAbs, bufferAbs, StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException(
					$"FileSystem 'path' ('{canonicalPath}') and '{StorageConnectionString.LocalBufferPathKey}' ('{localBufferPath}') resolve to the same directory. The local buffer must live in a different failure domain than the canonical storage (otherwise the buffer adds no resilience and doubles the cost of every write).");
		}

		private static void EnsureWritableDirectory(string path, string keyForError)
		{
			try
			{
				Directory.CreateDirectory(path);
			}
			catch (Exception ex)
			{
				throw new ArgumentException(
					$"Storage configuration key '{keyForError}' points to '{path}', which cannot be created or accessed as a directory: {ex.GetType().Name} — {ex.Message}", ex);
			}

			string probePath = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}.tmp");
			try
			{
				using (var fs = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					fs.WriteByte(0x01);
					fs.Flush(flushToDisk: true);
				}
			}
			catch (Exception ex)
			{
				throw new ArgumentException(
					$"Storage configuration key '{keyForError}' points to '{path}', but a write-probe failed: {ex.GetType().Name} — {ex.Message}. Ensure the path is mounted with write permissions (in Kubernetes, the PV must be writable by the pod).", ex);
			}
			finally
			{
				try { if (File.Exists(probePath)) File.Delete(probePath); } catch { }
			}
		}

		private static string NormalizeForComparison(string path)
		{
			string full = Path.GetFullPath(path);
			return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
	}
}
