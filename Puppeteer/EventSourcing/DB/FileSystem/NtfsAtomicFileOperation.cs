using System;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class NtfsAtomicFileOperation : IAtomicFileOperation
	{
		public void AtomicReplace(string tempPath, string targetPath)
		{
			if (tempPath == null) throw new ArgumentNullException(nameof(tempPath));
			if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

			if (!File.Exists(tempPath))
				throw new FileNotFoundException($"Temp file not found: {tempPath}");

			if (File.Exists(targetPath))
			{
				string backupPath = targetPath + ".bak";
				File.Replace(tempPath, targetPath, backupPath);
				try { File.Delete(backupPath); } catch { }
			}
			else
			{
				File.Move(tempPath, targetPath);
			}
		}

		public void RecoverFromIncompleteOperation(string targetPath)
		{
			if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

			string tempPath = targetPath + ".tmp";
			string backupPath = targetPath + ".bak";

			if (File.Exists(backupPath) && !File.Exists(targetPath))
			{
				File.Move(backupPath, targetPath);
			}

			if (File.Exists(tempPath))
			{
				try { File.Delete(tempPath); } catch { }
			}

			if (File.Exists(backupPath))
			{
				try { File.Delete(backupPath); } catch { }
			}
		}
	}
}
