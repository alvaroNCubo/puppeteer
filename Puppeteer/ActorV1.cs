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

		public async Task<string> PerformCmdAsync(string script, string ip, string user)
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

		public string ComandForDairy(String script, string ip, string user)
		{
			return base.Handler.ComandForDairy(script, ip, user);
		}

		internal string ComandForDairy(String script, Parameters arguments)
		{
			return base.Handler.ComandForDairy(script, arguments);
		}

		// B.3.4: configure automatic Script → Action promotion threshold.
		// V1 endpoints write Script-shaped text; once a candidate has been
		// observed N times (per the threshold) it is materialized into an
		// equivalent V2 Action, and subsequent live writes are journaled as
		// Invocation rows rather than Script rows. Default threshold is 30;
		// pass a lower value to promote sooner during ramp-up testing or a
		// higher one to keep recurrent endpoints as Scripts for longer.
		// Threshold must be >= 1.
		public void InternalAutomaticPromotion(int threshold)
		{
			base.Handler.SetPromotionCandidateThreshold(threshold);
		}

	}
}
