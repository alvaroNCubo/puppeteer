using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;

namespace Puppeteer.EventSourcing
{
	public class Script
	{
		private readonly Program program;
		private string script;
		private long entryId;
		private DateTime now;

		internal Script(string script, long entryId, DateTime now)
		{
			this.script = script;
			this.entryId = entryId;
			this.now = now;
		}

		internal Script(Program program)
		{
			this.program = program;
		}

		public string Text
		{
			get
			{
				return this.program == null ? this.script : this.program.Script;
			}
		}

		public long EntryId
		{
			get
			{
				return this.program == null ? this.entryId : this.program.EntryId;
			}
		}

		public DateTime Now
		{
			get
			{
				return this.program == null ? this.now : this.program.Now.Date;
			}
		}
	}
}
