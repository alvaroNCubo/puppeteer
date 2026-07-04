using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class ParserValidation
	{

		internal static void methodValidation(Type clazz, string methodName, Type[] signature)
		{
			bool methodExists = methodNameExistsInClass(clazz, methodName);
			if (!methodExists)
			{
				throw new LanguageException($"Function '{methodName}' is not defined on values of type '{clazz.Name}'. Please verify the function name and that it belongs to this type.", "", 1, 1);
			}
			else
			{
				bool atLeastOneMethodWithSameNameAndArgCount = atLeastOneMethodWithSameArgCount(clazz, methodName, signature);
				if (atLeastOneMethodWithSameNameAndArgCount)
				{
					validateErrorInMethodWithSameArgCount(clazz, methodName, signature);
				}
				else
				{
					validateErrorInMethodWithDifferentArgCount(clazz, methodName, signature);
				}
			}
		}

		private static void validateErrorInMethodWithDifferentArgCount(Type clazz, string methodName, Type[] signature)
		{
			List<MethodInfo> foundMethods = getDifferentSizeMethods(clazz, methodName);

			throw new LanguageException($"Function '{methodName}' is being called with the wrong number of arguments for type '{clazz.Name}'. {getSuggestedMethodHeaders(foundMethods)}", "", 1, 1);
		}

		private static string getSuggestedMethodHeaders(List<MethodInfo> foundMethods)
		{
			StringBuilder headers = new StringBuilder();
			headers.Append("Suggested overloads:");

			foreach (MethodInfo method in foundMethods)
			{
				string arguments = "";
				foreach (ParameterInfo type in method.GetParameters())
				{
					arguments += "" + type.Name + ":" + type.ParameterType.ToString() + ", ";
				}
				if (!String.IsNullOrEmpty(arguments))
				{
					arguments = arguments.Substring(0, arguments.Length - 2);
				}
				headers.Append(string.Format(" {0}({1}); ", method.Name, arguments));
			}
			return headers.ToString();
		}

		private static void validateErrorInMethodWithSameArgCount(Type objectClass, string methodName, Type[] signature)
		{
			Dictionary<int, MethodInfo> methodWeightsByErrors = new Dictionary<int, MethodInfo>();

			List<MethodInfo> foundMethods = getSameSizeMethods(objectClass, methodName, signature);

			foreach (MethodInfo method in foundMethods)
			{
				ParameterInfo[] expectedSignatureTemp = method.GetParameters();

				int errorCount = methodWeightsByErrors.Count;
				for (int i = 0; i < signature.Length; i++)
				{
					Type myClass = signature[i];
					ParameterInfo expectedClass = expectedSignatureTemp[i];

					bool areCompatible = myClass.IsAssignableFrom(expectedClass.ParameterType);

					if (!areCompatible &&
						myClass.IsGenericType && expectedClass.ParameterType.IsGenericType &&
						myClass.GetGenericArguments()[0] == expectedClass.ParameterType.GetGenericArguments()[0])
					{
						areCompatible = true;
					}
					if (!areCompatible)
					{
						errorCount++;
					}
				}
				methodWeightsByErrors[errorCount] = method;
			}

			List<int> keys = new List<int>(methodWeightsByErrors.Keys);
			keys.Sort();
			int methodWithFewestErrors = getSmallestKey(keys);

			StringBuilder errorMessage = new StringBuilder();
			foreach (int key in keys)
			{
				MethodInfo method = methodWeightsByErrors[key];
				ParameterInfo[] expectedSignatureTemp = method.GetParameters();

				if (key == methodWithFewestErrors)
				{
					for (int i = 0; i < signature.Length; i++)
					{
						Type myClass = signature[i];
						Type expectedClass = expectedSignatureTemp[i].ParameterType;

						bool areCompatible = myClass.IsAssignableFrom(expectedClass);

						if (!areCompatible &&
							myClass.IsGenericType && expectedClass.IsGenericType &&
							myClass.GetGenericArguments()[0] == expectedClass.GetGenericArguments()[0])
						{
							areCompatible = true;
						}
						if (!areCompatible)
						{
							errorMessage.Append($"Function '{methodName}' is being called with a value of type '{myClass.Name}' for parameter #{i + 1}, but the expected type is '{expectedClass.Name}'. Please correct it.");
						}
					}
				}
				else
				{
					errorMessage.Append("\n").Append($"Suggested overload: {getSuggestedMethodHeader(method)}");
				}
			}
			throw new LanguageException(errorMessage.ToString(), "", 1, 1);
		}

		private static int getSmallestKey(List<int> keys)
		{
			int lessThan = 0;
			foreach (int k in keys)
			{
				if (lessThan == 0 || k < lessThan)
				{
					lessThan = k;
				}
			}
			return lessThan;
		}

		private static string getSuggestedMethodHeader(MethodInfo method)
		{
			string arguments = "";
			foreach (ParameterInfo type in method.GetParameters())
			{
				arguments += "" + type.Name + ":" + type.ParameterType + ", ";
			}
			arguments = arguments.Substring(0, arguments.Length - 2);

			return string.Format(" {0}({1})", method.Name, arguments);
		}

		private static List<MethodInfo> getSameSizeMethods(Type clazz, string methodName, Type[] signature)
		{
			List<MethodInfo> foundMethods = new List<MethodInfo>();
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool isSameName = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (isSameName)
				{
					bool haveSameArgCount = method.GetParameters().Length == signature.Length;
					if (haveSameArgCount)
					{
						foundMethods.Add(method);
					}
				}
			}
			return foundMethods;
		}

		private static List<MethodInfo> getDifferentSizeMethods(Type clazz, string methodName)
		{
			List<MethodInfo> foundMethods = new List<MethodInfo>();
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool isSameName = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (isSameName)
				{
					foundMethods.Add(method);
				}
			}
			return foundMethods;
		}

		private static bool atLeastOneMethodWithSameArgCount(Type clazz, string methodName, Type[] signature)
		{
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool isSameName = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (isSameName)
				{
					bool haveSameArgCount = method.GetParameters().Length == signature.Length;
					if (haveSameArgCount)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool methodNameExistsInClass(Type clazz, string methodName)
		{
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool isSameName = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (isSameName)
				{
					return true;
				}
			}
			return false;
		}
	}
}
