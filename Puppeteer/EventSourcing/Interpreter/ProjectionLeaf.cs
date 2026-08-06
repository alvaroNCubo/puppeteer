using System;
using System.Collections;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Interpreter
{
	// The output plane emits VALUES; the projection over the material is the actor's
	// to author. When a whole domain object reaches an output statement there is no
	// rendering the framework can produce without asking the object to render itself
	// -- ToString(), or a render method found by reflection on the instance -- and
	// that hands the projection to the material, which never chose to own it. So the
	// framework refuses to infer one: the author either walks the object and emits
	// the leaves the view needs, or states the rendering explicitly (obj.ToString()),
	// which yields a value again and passes.
	//
	// A leaf is one of the primitives the language speaks, or a sequence of leaves.
	// An UNKNOWN type (null, or object) is not material: the static type simply does
	// not say. Those are left to the sink guard, which sees the runtime type.
	internal static class ProjectionLeaf
	{
		internal static bool IsUnknown(Type type)
		{
			return type == null || type == typeof(object);
		}

		internal static bool IsLeaf(Type type)
		{
			if (type == null) return false;

			Type underlying = Nullable.GetUnderlyingType(type);
			if (underlying != null) type = underlying;

			if (type == typeof(string) || type == typeof(char) || type == typeof(bool)
				|| type == typeof(int) || type == typeof(long) || type == typeof(double)
				|| type == typeof(decimal) || type == typeof(DateTime) || type.IsEnum)
			{
				return true;
			}

			Type element = ElementTypeOf(type);
			if (element != null) return IsUnknown(element) || IsLeaf(element);

			return false;
		}

		// Material is what the static type KNOWS is not a value: neither a leaf nor
		// undecided. A sequence is material when its elements are.
		internal static bool IsMaterial(Type type)
		{
			return !IsUnknown(type) && !IsLeaf(type);
		}

		// The element type of a sequence, or null when the type is not one. A
		// non-generic sequence yields object -- unknown, decided at the sink.
		private static Type ElementTypeOf(Type type)
		{
			if (type.IsArray) return type.GetElementType();

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
			{
				return type.GetGenericArguments()[0];
			}

			foreach (Type contract in type.GetInterfaces())
			{
				if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
				{
					return contract.GetGenericArguments()[0];
				}
			}

			if (typeof(IEnumerable).IsAssignableFrom(type)) return typeof(object);

			return null;
		}

		// Authoring-time refusal: the static type of the emitted expression is known
		// to be material.
		internal static LanguageException MaterialCannotBeProjected(string command, string alias, Type type)
		{
			return new LanguageException(
				$"'{command} {alias}' emits a value of type '{Name(type)}', which is material, not a value. " +
				$"The projection is the actor's to author, not the material's: emit the leaves the view needs, " +
				$"or state the rendering explicitly (for instance '.ToString()').");
		}

		// Sink guard: the static type did not say, and the value that arrived is
		// material. Same refusal, one step later.
		internal static LanguageException MaterialReachedTheSink(Type type)
		{
			return new LanguageException(
				$"A value of type '{Name(type)}' reached the output sink, which emits values, not material. " +
				$"The projection is the actor's to author, not the material's: emit the leaves the view needs, " +
				$"or state the rendering explicitly (for instance '.ToString()').");
		}

		private static string Name(Type type)
		{
			return type == null ? "unknown" : type.Name;
		}
	}
}
