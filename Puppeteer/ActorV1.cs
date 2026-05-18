using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Puppeteer
{
	public class ActorV1 : Actor
	{
		public ActorV1(string name) : base(name)
		{
		}

		public ActorV1(string name, params Assembly[] libraryAssemblies)
			: base(name, libraryAssemblies)
		{
		}

		public delegate void LeaderInitializationHandler();
		public delegate void AfterRecoveringHandler(DatabaseType dbType, string connection, string persona, long lastRecoveredId);

		public AfterRecoveringHandler OnAfterRecovering
		{
			get
			{
				return base.Handler.OnAfterRecovering;
			}
			set
			{
				base.Handler.OnAfterRecovering = value;
			}
		}

		public LeaderInitializationHandler OnLeaderInitialization
		{
			get
			{
				return base.Handler.OnLeaderInitialization;
			}
			set
			{
				base.Handler.OnLeaderInitialization = value;
			}
		}

		public async Task<string> PerformCmdAsync(string script, IpAddress ip, UserInLog user)
		{
			return await base.Handler.PerformCmdAsync(script, ip, user);
		}

		internal string PerformChk(string script, Parameters arguments)
		{
			return base.Handler.PerformChk(script, arguments);
		}

		internal string PerformQry(string script, Parameters parameters)
		{
			return base.Handler.PerformQry(script, parameters);
		}

		internal async Task<string> PerformCmdAsync(string script, Parameters parameters)
		{
			return await base.Handler.PerformCmdAsync(script, parameters);
		}

		public string ComandForDairy(String script, IpAddress ip, UserInLog user)
		{
			return base.Handler.ComandForDairy(script, ip, user);
		}

		internal string ComandForDairy(String script, Parameters arguments)
		{
			return base.Handler.ComandForDairy(script, arguments);
		}

	}
}
