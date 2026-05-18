using System.Runtime.InteropServices;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal static class AtomicFileOperationFactory
	{
		internal static IAtomicFileOperation Create()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return new NtfsAtomicFileOperation();

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
				return new PosixAtomicFileOperation();

			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return new PosixAtomicFileOperation();

			return new SafeAtomicFileOperation();
		}
	}
}
