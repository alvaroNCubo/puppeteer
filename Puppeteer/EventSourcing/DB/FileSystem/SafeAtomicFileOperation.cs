using System;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class SafeAtomicFileOperation : IAtomicFileOperation
	{
		public void AtomicReplace(string tempPath, string targetPath)
		{
			if (tempPath == null) throw new ArgumentNullException(nameof(tempPath));
			if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

			if (!File.Exists(tempPath))
				throw new FileNotFoundException($"Temp file not found: {tempPath}");

			string backupPath = targetPath + ".bak";

			if (File.Exists(targetPath))
			{
				if (File.Exists(backupPath))
				{
					File.Delete(backupPath);
				}
				File.Move(targetPath, backupPath);
			}

			File.Move(tempPath, targetPath);

			if (File.Exists(backupPath))
			{
				try { File.Delete(backupPath); } catch { }
			}
		}

		public void RecoverFromIncompleteOperation(string targetPath)
		{
			if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

			string tempPath = targetPath + ".tmp";
			string backupPath = targetPath + ".bak";

			if (!File.Exists(targetPath) && File.Exists(backupPath))
			{
				File.Move(backupPath, targetPath);
			}
			else if (!File.Exists(targetPath) && File.Exists(tempPath))
			{
				File.Move(tempPath, targetPath);
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
