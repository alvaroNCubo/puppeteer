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
			// Record the name of the ingested assembly for NewInstance diagnostics:
			// when a class is not found, listing the assemblies that DID load is the
			// detail that makes a bad library configuration obvious (e.g. a follower
			// that passes the host Performance assembly instead of the domain one).
			string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? assembly.ToString();
			if (!ingestedAssemblyNames.Contains(assemblyName))
			{
				ingestedAssemblyNames.Add(assemblyName);
			}

			foreach (var type in LoadTypesFromAssembly(assembly))
			{
				// An interface enters the TYPE table only, never the CLASS table. The two tables
				// answer different questions: classesByName answers "what can be instantiated or
				// used as a static receiver", and no interface can be either; typesByName answers
				// "what type does this name denote", which is what a receiver-type hint in a Reaction
				// pattern needs in order to name the declared type of a receiver obtained from a
				// factory whose return type is an interface. Admitting an interface into
				// classesByName would make it look constructible and would replace the precise
				// "class not found" diagnostic with a constructor-resolution failure.
				if (!type.IsInterface)
				{
					var classInfo = new ClassInfo(type);

					if (!classesByName.TryGetValue(type.Name, out var classList))
					{
						classList = new List<ClassInfo>();
						classesByName.Add(type.Name, classList);
					}
					classList.Add(classInfo);
				}

				// If two assemblies declare a Type with the same Name, the first one wins (stable and predictable).
				// FindClassesByName returns both via classesByName if the caller needs to resolve the ambiguity.
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
				// Includes domain classes, ENUMS and INTERFACES. Enums are indexed by name so that
				// the explicit cast (MyEnum)'Value' resolves them via GetTypeOrThrow. The default
				// path (parameter/literal/symbol in an enum slot) does NOT depend on this: there
				// the enum type is discovered from the method/constructor signature.
				// Interfaces are indexed so that a receiver-type hint in a Reaction pattern can
				// name the declared type of a receiver whose static type IS an interface. Without
				// them such a receiver is unnameable: the interface is the only type on its is-a
				// closure, so no other name can ever match it, and the pattern silently never
				// fires. IsClass is false for an interface, which is what used to drop it here
				// (IsSubclassOf(object) is true for an interface and never was the discriminator).
				// IngestAssembly keeps interfaces out of the CLASS table — they are named, not
				// instantiated.
				// The visibility gate asks what to EXCLUDE, not what to include. The inclusion list it
				// replaces (IsPublic || IsNestedPublic || (IsNotPublic && !IsNestedPrivate)) could never
				// admit a nested non-public type: IsNotPublic tests the visibility mask against
				// TypeAttributes.NotPublic, which is 0x0 and therefore reachable only by a top-level
				// type. A nested type always carries NestedPublic/NestedPrivate/NestedFamily/
				// NestedAssembly/NestedFamANDAssem/NestedFamORAssem instead, so an assembly-visible
				// nested type failed all three tests and was dropped with no diagnostic, surfacing much
				// later as a type the registry does not know. The dead !IsNestedPrivate conjunct records
				// the intent that was never met: admit every visibility except nested-private. This
				// single test is exactly that set, and it keeps out the nested-private types the
				// compiler emits for closures and state machines.
				if (!t.IsNestedPrivate && ((t.IsClass && t.IsSubclassOf(typeof(object))) || t.IsEnum || t.IsInterface))
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

		// All domain types known to the library (one per distinct simple name, first-wins).
		// Used by static pattern validation to scan a receiver type's descendants and the
		// static classes that may declare extension methods over it.
		internal IEnumerable<Type> AllTypes => typesByName.Values;

		// Simple names of the assemblies ingested into this library. Used only by
		// NewInstance diagnostics when reporting a missing class.
		internal IReadOnlyList<string> LoadedAssemblyNames => ingestedAssemblyNames;

		// Simple names of the classes known to the library. Used only by NewInstance
		// diagnostics (bounded preview) to suggest what IS loaded.
		internal IEnumerable<string> KnownClassNames => classesByName.Keys;
	}
}
