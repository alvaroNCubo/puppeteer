using System;

namespace Puppeteer
{
	public class UserInLog
	{
		private readonly string id;
		public static readonly UserInLog ANONYMOUS = new UserInLog("Anonymous");

		internal UserInLog(string id)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(id);

			this.id = id;
		}

		public static UserInLog GenerateUserBasedOn(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return ANONYMOUS;
			}
			else
			{
				return new UserInLog(id);
			}
		}

		public string Id
		{
			get
			{
				return id;
			}
		}

		public string ToMySQLFormat()
		{
			return "\'" + id + "\'";
		}

		/*void IOperations.Print(StringBuilder output)
		{
			output.Append('\'');
			output.Append(id);
			output.Append('\'');
		}*/

		public override string ToString()
		{
			return "\'" + id + "\'";
		}
	}


}
