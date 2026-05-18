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
		internal ParameterKind Kind { get; }

		internal ParameterDescriptor(int parameterModifier, string name, Type parameterType, ParameterKind kind)
		{
			ArgumentNullException.ThrowIfNull(name);
			if (!Parameter.IsValidParameterName(name)) throw new LanguageException($"Parameter name '{name}' is not valid");
			if (parameterModifier < 1) throw new LanguageException($"Modify '{parameterModifier}' is not valid");

			Name = name;
			ParameterType = parameterType;
			ParameterModifier = parameterModifier;
			Kind = kind;
		}
	}

	internal class ParameterSignature : IEnumerable<ParameterDescriptor>
	{
		private readonly ParameterDescriptor[] _parameterDescriptors;

		internal ParameterSignature(IEnumerable<Parameter> referencedParameters)
		{
			ArgumentNullException.ThrowIfNull(referencedParameters);

			var descriptors = referencedParameters
				.Select(p => new ParameterDescriptor(p.ParameterModifier, p.Name, p.ParameterType, p.Kind))
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
				if (parameter.ParameterType != descriptor.ParameterType || parameter.ParameterModifier != descriptor.ParameterModifier)
					return false;
			}
			return true;
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
