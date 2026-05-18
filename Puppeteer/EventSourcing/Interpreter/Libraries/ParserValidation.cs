using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class ParserValidation
	{

		internal static void validacionDeMetodo(Type clazz, string methodName, Type[] signature)
		{
			bool existeElNombreDelMetodoEnLaClase = existeELNombreDelMetodoEnLaClase(clazz, methodName);
			if (!existeElNombreDelMetodoEnLaClase)
			{
				throw new LanguageException($"Function '{methodName}' is not defined on values of type '{clazz.Name}'. Please verify the function name and that it belongs to this type.", "", 1, 1);
			}
			else
			{
				bool existeAlMenosUnMetodoConEseNombreYConLaMisMaCantidadDeArgumentos = existeAlMenosUnMetodoConLaMismaCantidadArgumentos(clazz, methodName, signature);
				if (existeAlMenosUnMetodoConEseNombreYConLaMisMaCantidadDeArgumentos)
				{
					validaErrorEnMetodoConMismaCantidadDeArgumentos(clazz, methodName, signature);
				}
				else
				{
					validaErrorEnMetodoConDiferenteCantidadDeArgumentos(clazz, methodName, signature);
				}
			}
		}

		private static void validaErrorEnMetodoConDiferenteCantidadDeArgumentos(Type clazz, string methodName, Type[] signature)
		{
			List<MethodInfo> metodosEncontrados = obtenerMetodosDiferenteTamanno(clazz, methodName);

			throw new LanguageException($"Function '{methodName}' is being called with the wrong number of arguments for type '{clazz.Name}'. {obtenerEncabezadosDeMetodosSugeridos(metodosEncontrados)}", "", 1, 1);
		}

		private static string obtenerEncabezadosDeMetodosSugeridos(List<MethodInfo> metodosEncontrados)
		{
			StringBuilder encabezados = new StringBuilder();
			encabezados.Append("Suggested overloads:");

			foreach (MethodInfo method in metodosEncontrados)
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
				encabezados.Append(string.Format(" {0}({1}); ", method.Name, arguments));
			}
			return encabezados.ToString();
		}

		private static void validaErrorEnMetodoConMismaCantidadDeArgumentos(Type claseDelObjeto, string methodName, Type[] signature)
		{
			Dictionary<int, MethodInfo> pesosDeMetodosPorErrores = new Dictionary<int, MethodInfo>();

			List<MethodInfo> metodosEncontrados = obtenerMetodosMismoTamanno(claseDelObjeto, methodName, signature);

			foreach (MethodInfo method in metodosEncontrados)
			{
				ParameterInfo[] firmaEsperadaTemp = method.GetParameters();

				int cantidadErrores = pesosDeMetodosPorErrores.Count;
				for (int i = 0; i < signature.Length; i++)
				{
					Type miClase = signature[i];
					ParameterInfo claseEsperada = firmaEsperadaTemp[i];

					bool sonCompatibles = miClase.IsAssignableFrom(claseEsperada.ParameterType);

					if (!sonCompatibles &&
						miClase.IsGenericType && claseEsperada.ParameterType.IsGenericType &&
						miClase.GetGenericArguments()[0] == claseEsperada.ParameterType.GetGenericArguments()[0])
					{
						sonCompatibles = true;
					}
					if (!sonCompatibles)
					{
						cantidadErrores++;
					}
				}
				pesosDeMetodosPorErrores[cantidadErrores] = method;
			}

			List<int> keys = new List<int>(pesosDeMetodosPorErrores.Keys);
			keys.Sort();
			int metodoConMenosCantidadDeErrores = obtenerKeyMenor(keys);

			StringBuilder mensajeDeError = new StringBuilder();
			foreach (int key in keys)
			{
				MethodInfo method = pesosDeMetodosPorErrores[key];
				ParameterInfo[] firmaEsperadaTemp = method.GetParameters();

				if (key == metodoConMenosCantidadDeErrores)
				{
					for (int i = 0; i < signature.Length; i++)
					{
						Type miClase = signature[i];
						Type claseEsperada = firmaEsperadaTemp[i].ParameterType;

						bool sonCompatibles = miClase.IsAssignableFrom(claseEsperada);

						if (!sonCompatibles &&
							miClase.IsGenericType && claseEsperada.IsGenericType &&
							miClase.GetGenericArguments()[0] == claseEsperada.GetGenericArguments()[0])
						{
							sonCompatibles = true;
						}
						if (!sonCompatibles)
						{
							mensajeDeError.Append($"Function '{methodName}' is being called with a value of type '{miClase.Name}' for parameter #{i + 1}, but the expected type is '{claseEsperada.Name}'. Please correct it.");
						}
					}
				}
				else
				{
					mensajeDeError.Append("\n").Append($"Suggested overload: {obtenerEncabezadoMetodoSugerido(method)}");
				}
			}
			throw new LanguageException(mensajeDeError.ToString(), "", 1, 1);
		}

		private static int obtenerKeyMenor(List<int> keys)
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

		private static string obtenerEncabezadoMetodoSugerido(MethodInfo method)
		{
			string arguments = "";
			foreach (ParameterInfo type in method.GetParameters())
			{
				arguments += "" + type.Name + ":" + type.ParameterType + ", ";
			}
			arguments = arguments.Substring(0, arguments.Length - 2);

			return string.Format(" {0}({1})", method.Name, arguments);
		}

		private static List<MethodInfo> obtenerMetodosMismoTamanno(Type clazz, string methodName, Type[] signature)
		{
			List<MethodInfo> metodosEncontrados = new List<MethodInfo>();
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool esElMismoNombre = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (esElMismoNombre)
				{
					bool poseenLaMismaCantidadDeArgumentos = method.GetParameters().Length == signature.Length;
					if (poseenLaMismaCantidadDeArgumentos)
					{
						metodosEncontrados.Add(method);
					}
				}
			}
			return metodosEncontrados;
		}

		private static List<MethodInfo> obtenerMetodosDiferenteTamanno(Type clazz, string methodName)
		{
			List<MethodInfo> metodosEncontrados = new List<MethodInfo>();
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool esElMismoNombre = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (esElMismoNombre)
				{
					metodosEncontrados.Add(method);
				}
			}
			return metodosEncontrados;
		}

		private static bool existeAlMenosUnMetodoConLaMismaCantidadArgumentos(Type clazz, string methodName, Type[] signature)
		{
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool esElMismoNombre = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (esElMismoNombre)
				{
					bool poseenLaMismaCantidadDeArgumentos = method.GetParameters().Length == signature.Length;
					if (poseenLaMismaCantidadDeArgumentos)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool existeELNombreDelMetodoEnLaClase(Type clazz, string methodName)
		{
			foreach (MethodInfo method in clazz.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool esElMismoNombre = string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase);
				if (esElMismoNombre)
				{
					return true;
				}
			}
			return false;
		}
	}
}
