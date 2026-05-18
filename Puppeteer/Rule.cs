using Puppeteer.EventSourcing;

namespace Puppeteer
{
	public abstract class Rule
	{
		public virtual void Init() { }

		public virtual void Finish() { }

		public virtual void PartialUpdate() { }

		public ActorFollower Follower { get; set; }
	}

	public abstract class RuleWithActor : Rule
	{
		public abstract void When(Script script, Actor actor);

		public abstract void Then(Script script, Actor actor);

	}

	public abstract class RuleWithoutActor : Rule
	{
		public abstract void When(Script script);

		public abstract void Then(Script script);
	}
}
