using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	internal readonly struct ParameterDescriptor
	{
		internal string Name { get; }
		internal Type ParameterType { get; }
		internal int ParameterModifier { get; }

		internal ParameterDescriptor(int parameterModifier, string name, Type parameterType)
		{
			ArgumentNullException.ThrowIfNull(name);
			if (!Parameter.IsValidParameterName(name)) throw new LanguageException($"Parameter name '{name}' is not valid");
			if (parameterModifier < 1) throw new LanguageException($"Modify '{parameterModifier}' is not valid");

			Name = name;
			ParameterType = parameterType;
			ParameterModifier = parameterModifier;
		}
	}

	internal class ParameterSignature : IEnumerable<ParameterDescriptor>
	{
		private readonly ParameterDescriptor[] _parameterDescriptors;

		internal ParameterSignature(IEnumerable<Parameter> referencedParameters)
		{
			ArgumentNullException.ThrowIfNull(referencedParameters);

			var descriptors = referencedParameters
				.Select(p => new ParameterDescriptor(p.ParameterModifier, p.Name, p.ParameterType))
				.ToArray();

			_parameterDescriptors = descriptors;
		}

		public IEnumerator<ParameterDescriptor> GetEnumerator() => ((IEnumerable<ParameterDescriptor>)_parameterDescriptors).GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		internal bool IsCompatible(Parameters parameters)
		{
			foreach (var descriptor in _parameterDescriptors)
			{
				if (!parameters.ContainsParameter(descriptor.Name))
					return false;
				var parameter = parameters[descriptor.Name];
				if (parameter.ParameterType != descriptor.ParameterType)
					return false;
				if (!ModifiersAreCompatible(parameter.ParameterModifier, descriptor.ParameterModifier))
					return false;
			}
			return true;
		}

		// In (caller-supplied literal) and Eval (value computed transactionally from the
		// actor state at command time) are both VALUE-IN kinds: each supplies a value that
		// the body consumes through a bare identifier bound as a local parameter, and the
		// body cannot tell them apart. They are therefore interchangeable for signature
		// compatibility. This matters across the define-action round-trip: the Define header
		// carries the Out/InOut modifier explicitly but deliberately NOT In or Eval (Eval's
		// script is not in the header; its computed value travels in the arguments blob and
		// is reconstructed as a value-in argument). So a rehydrated Eval parameter comes
		// back as In, while the first live re-issue after a restart still supplies the
		// original Eval modifier. Rejecting that mismatch blocked every post-restart
		// invocation of an Eval-parametric Action. Out / InOut carry write-back semantics
		// that In / Eval do not, so those remain matched strictly — and because the header
		// now preserves them, a rehydrated Out/InOut parameter keeps its modifier and this
		// strict match holds after a restart.
		private static bool ModifiersAreCompatible(int provided, int expected)
		{
			if (provided == expected) return true;
			return IsValueInModifier(provided) && IsValueInModifier(expected);
		}

		private static bool IsValueInModifier(int modifier)
		{
			return modifier == Parameter.In || modifier == Parameter.Eval;
		}

		private IEnumerable<Parameter> ReferencedParameters(Parameters parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);

			var referencedParameters = new List<Parameter>();
			foreach (var parameter in parameters)
			{
				bool exists = false;
				foreach (var p in referencedParameters)
				{
					if (String.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
					{
						exists = true;
						break;
					}
				}
				if (!exists)
				{
					bool foundInSignature = false;
					foreach (var d in _parameterDescriptors)
					{
						if (String.Equals(d.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
						{
							foundInSignature = true;
							break;
						}
					}
					if (foundInSignature)
					{
						referencedParameters.Add(parameter);
					}
				}
			}
			return referencedParameters;
		}
	}
}
