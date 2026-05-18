using System;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class PosixAtomicFileOperation : IAtomicFileOperation
	{
		public void AtomicReplace(string tempPath, string targetPath)
		{
			if (tempPath == null) throw new ArgumentNullException(nameof(tempPath));
			if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

			if (!File.Exists(tempPath))
				throw new FileNotFoundException($"Temp file not found: {tempPath}");

			File.Move(tempPath, targetPath, overwrite: true);
		}

		public void RecoverFromIncompleteOperation(string targetPath)
		{
			if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

			string tempPath = targetPath + ".tmp";

			if (File.Exists(tempPath))
			{
				try { File.Delete(tempPath); } catch { }
			}
		}
	}
}
