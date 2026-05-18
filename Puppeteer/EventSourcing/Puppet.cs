using System;

namespace Puppeteer.EventSourcing
{
	[System.AttributeUsage(
		AttributeTargets.Class |
		AttributeTargets.Constructor |
		AttributeTargets.Enum |
		AttributeTargets.Field |
		AttributeTargets.Method |
		AttributeTargets.Property
	 )]
	public class Puppet : System.Attribute
	{
	}
}
