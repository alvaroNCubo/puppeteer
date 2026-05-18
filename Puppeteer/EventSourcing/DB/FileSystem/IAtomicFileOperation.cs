namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal interface IAtomicFileOperation
	{
		void AtomicReplace(string tempPath, string targetPath);

		void RecoverFromIncompleteOperation(string targetPath);
	}
}
