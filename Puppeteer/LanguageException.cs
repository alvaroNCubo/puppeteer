using System;

namespace Puppeteer
{

	public class LanguageException : Exception
	{

		private const long serialVersionUID = 2081427811811732501L;
		internal int row_Renamed;
		internal int column_Renamed;

		public LanguageException(string message, string lineWithError, int row, int column) : base(message + "\r" + lineWithError)
		{
			row_Renamed = row;
			column_Renamed = column;
		}

		public LanguageException(string message) : base(message)
		{
			row_Renamed = 0;
			column_Renamed = 0;
		}

		public string lineWithError()
		{
			return base.Message;
		}

		public int row()
		{
			return row_Renamed;
		}

		public int column()
		{
			return column_Renamed;
		}
	}

}
