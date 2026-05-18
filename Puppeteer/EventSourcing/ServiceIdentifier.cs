[System.AttributeUsage(System.AttributeTargets.Method)]

public class ServiceIdentifier : System.Attribute
{
	private string methodName;

	public ServiceIdentifier(string methodName)
	{
		this.methodName = methodName;
	}
}
