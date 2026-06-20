using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Puppeteer.EventSourcing
{
	internal class DomainLibraries
	{
		internal class ClassInfo
		{
			internal Type Type { get; }
			internal string Namespace { get; }
			internal string FullName { get; }

			internal ClassInfo(Type type)
			{
				if (type == null) throw new ArgumentNullException(nameof(type));
				Type = type;
				Namespace = type.Namespace ?? string.Empty;
				FullName = type.FullName ?? type.Name;
			}
		}

		private static readonly Dictionary<Assembly, DomainLibraries> librariesByAssembly = new Dictionary<Assembly, DomainLibraries>();
		private static readonly Dictionary<string, DomainLibraries> librariesByAssemblySet = new Dictionary<string, DomainLibraries>();
		private static readonly object myLock = new object();

		private readonly Dictionary<string, Type> typesByName;
		private readonly Dictionary<string, List<ClassInfo>> classesByName;
		private readonly List<string> ingestedAssemblyNames;

		private DomainLibraries(Assembly assembly)
		{
			if (assembly == null) throw new ArgumentNullException(nameof(assembly));

			typesByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
			classesByName = new Dictionary<string, List<ClassInfo>>(StringComparer.OrdinalIgnoreCase);
			ingestedAssemblyNames = new List<string>();

			IngestAssembly(assembly);
		}

		private DomainLibraries(Assembly[] assemblies)
		{
			if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));

			typesByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
			classesByName = new Dictionary<string, List<ClassInfo>>(StringComparer.OrdinalIgnoreCase);
			ingestedAssemblyNames = new List<string>();

			foreach (var assembly in assemblies.Where(a => a != null).Distinct())
			{
				IngestAssembly(assembly);
			}
		}

		private void IngestAssembly(Assembly assembly)
		{
			// Registramos el nombre del assembly ingerido para el diagnostico de
			// NewInstance: cuando una clase no se encuentra, listar los assemblies
			// que SI se cargaron es el dato que hace obvia una mala configuracion de
			// librerias (p.ej. follower que pasa el assembly de la app en vez del del dominio).
			string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? assembly.ToString();
			if (!ingestedAssemblyNames.Contains(assemblyName))
			{
				ingestedAssemblyNames.Add(assemblyName);
			}

			foreach (var type in LoadTypesFromAssembly(assembly))
			{
				var classInfo = new ClassInfo(type);

				if (!classesByName.TryGetValue(type.Name, out var classList))
				{
					classList = new List<ClassInfo>();
					classesByName.Add(type.Name, classList);
				}
				classList.Add(classInfo);

				// Si dos assemblies declaran un Type con el mismo Name, gana el primero (estable y predecible).
				// FindClassesByName devuelve ambos via classesByName si el caller necesita resolver ambig�edad.
				if (!typesByName.ContainsKey(type.Name))
				{
					typesByName.Add(type.Name, type);
				}
			}
		}

		internal static DomainLibraries GetOrLoad(Assembly assembly)
		{
			if (assembly == null) throw new ArgumentNullException(nameof(assembly));

			lock (myLock)
			{
				if (!librariesByAssembly.TryGetValue(assembly, out var libraries))
				{
					libraries = new DomainLibraries(assembly);
					librariesByAssembly.Add(assembly, libraries);
				}
				return libraries;
			}
		}

		internal static DomainLibraries GetOrLoad(params Assembly[] assemblies)
		{
			if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
			if (assemblies.Length == 0)
				throw new ArgumentException("At least one assembly is required.", nameof(assemblies));

			if (assemblies.Length == 1) return GetOrLoad(assemblies[0]);

			string key = string.Join("|", assemblies
				.Where(a => a != null)
				.Select(a => a.FullName)
				.Distinct()
				.OrderBy(n => n, StringComparer.Ordinal));

			lock (myLock)
			{
				if (!librariesByAssemblySet.TryGetValue(key, out var libraries))
				{
					libraries = new DomainLibraries(assemblies);
					librariesByAssemblySet.Add(key, libraries);
				}
				return libraries;
			}
		}

		private List<Type> LoadTypesFromAssembly(Assembly assembly)
		{
			List<Type> result = new List<Type>();
			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				// Tolerate transitive package dependencies missing from the host bin.
				// Required for labs that load external open-source domain assemblies
				// (e.g. eShop Ordering.Domain references MediatR; not all bins ship it).
				types = ex.Types.Where(t => t != null).ToArray();
			}
			foreach (Type t in types)
			{
				// Incluye clases y ENUMS del dominio. Los enums se indexan por nombre para que el
				// cast explicito (MiEnum)'Valor' los resuelva via GetTypeOrThrow. El path por
				// defecto (parametro/literal/simbolo en slot enum) NO depende de esto: alli el tipo
				// del enum se descubre de la firma del metodo/constructor.
				if ((t.IsPublic || t.IsNestedPublic || (t.IsNotPublic && !t.IsNestedPrivate)) && ((t.IsClass && t.IsSubclassOf(typeof(object))) || t.IsEnum))
				{
					result.Add(t);
				}
			}
			return result;
		}

		internal bool Knows(string className)
		{
			if (String.IsNullOrEmpty(className)) throw new ArgumentNullException(nameof(className));
			return typesByName.ContainsKey(className);
		}

		internal bool TryGetType(string name, out Type type)
		{
			if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
			return typesByName.TryGetValue(name, out type);
		}

		internal Type GetTypeOrThrow(string name)
		{
			if (String.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
			if (!typesByName.TryGetValue(name, out var type))
			{
				throw new KeyNotFoundException($"Type '{name}' was not found in the loaded domain libraries.");
			}
			return type;
		}

		internal IEnumerable<ClassInfo> FindClassesByName(string className)
		{
			if (String.IsNullOrEmpty(className)) throw new ArgumentNullException(nameof(className));
			return classesByName.TryGetValue(className, out var classes) ? classes : Enumerable.Empty<ClassInfo>();
		}

		internal bool TryFindClassesByName(string className, out IEnumerable<ClassInfo> classes)
		{
			if (String.IsNullOrEmpty(className)) throw new ArgumentNullException(nameof(className));
			if (classesByName.TryGetValue(className, out var classList))
			{
				classes = classList;
				return true;
			}
			classes = Enumerable.Empty<ClassInfo>();
			return false;
		}

		internal int TypeCount => typesByName.Count;

		// Nombres (simples) de los assemblies ingeridos en esta libreria. Usado solo
		// por el diagnostico de NewInstance al reportar una clase ausente.
		internal IReadOnlyList<string> LoadedAssemblyNames => ingestedAssemblyNames;

		// Nombres simples de las clases conocidas por la libreria. Usado solo por el
		// diagnostico de NewInstance (preview acotado) para sugerir lo que SI esta cargado.
		internal IEnumerable<string> KnownClassNames => classesByName.Keys;
	}
}
