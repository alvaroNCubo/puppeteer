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

	// A pattern-AUTHORING error (as opposed to a data-driven match/no-match outcome):
	// the pattern cannot yield a binding for the observed event no matter its data —
	// e.g. a $-capture placed over an argument that carries no journaled value (a
	// global/local variable or an operated expression). It must PROPAGATE (surface to
	// logs / fail the batch) rather than be swallowed as a silent no-match, so the
	// author sees the mistake instead of a reaction that quietly never fires. Pattern.Match
	// re-throws this, mirroring how it already re-throws the OUT-parameter authoring error.
	public sealed class PatternCaptureException : LanguageException
	{
		public PatternCaptureException(string message) : base(message) { }
	}

}
