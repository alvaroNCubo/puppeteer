using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Puppeteer.EventSourcing.Follower
{
	// Represents a domain type left unresolved during parsing.
	// Used as a placeholder until the matching phase, where the real types are resolved.
	internal class UnresolvedDomainType : Type
	{
		private readonly string typeName;

		internal UnresolvedDomainType(string typeName)
		{
			ArgumentNullException.ThrowIfNull(typeName);

			this.typeName = typeName;
		}

		internal string TypeName => typeName;

		public override string Name => typeName;
		public override string FullName => typeName;
		public override string Namespace => null;
		public override Type BaseType => typeof(object);
		public override Type UnderlyingSystemType => this;
		public override Assembly Assembly => throw new NotSupportedException("UnresolvedDomainType no tiene Assembly asociado hasta la fase de matching");
		public override Guid GUID => throw new NotSupportedException();
		public override Module Module => throw new NotSupportedException();
		public override string AssemblyQualifiedName => throw new NotSupportedException();

		protected override TypeAttributes GetAttributeFlagsImpl() => TypeAttributes.Public;
		protected override ConstructorInfo GetConstructorImpl(BindingFlags bindingAttr, Binder binder, CallingConventions callConvention, Type[] types, ParameterModifier[] modifiers) => throw new NotSupportedException();
		public override ConstructorInfo[] GetConstructors(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type GetElementType() => throw new NotSupportedException();
		public override EventInfo GetEvent(string name, BindingFlags bindingAttr) => throw new NotSupportedException();
		public override EventInfo[] GetEvents(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override FieldInfo GetField(string name, BindingFlags bindingAttr) => throw new NotSupportedException();
		public override FieldInfo[] GetFields(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type GetInterface(string name, bool ignoreCase) => throw new NotSupportedException();
		public override Type[] GetInterfaces() => throw new NotSupportedException();
		public override MemberInfo[] GetMembers(BindingFlags bindingAttr) => throw new NotSupportedException();
		protected override MethodInfo GetMethodImpl(string name, BindingFlags bindingAttr, Binder binder, CallingConventions callConvention, Type[] types, ParameterModifier[] modifiers) => throw new NotSupportedException();
		public override MethodInfo[] GetMethods(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type GetNestedType(string name, BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type[] GetNestedTypes(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override PropertyInfo[] GetProperties(BindingFlags bindingAttr) => throw new NotSupportedException();
		protected override PropertyInfo GetPropertyImpl(string name, BindingFlags bindingAttr, Binder binder, Type returnType, Type[] types, ParameterModifier[] modifiers) => throw new NotSupportedException();
		protected override bool HasElementTypeImpl() => false;
		protected override bool IsArrayImpl() => false;
		protected override bool IsByRefImpl() => false;
		protected override bool IsCOMObjectImpl() => false;
		protected override bool IsPointerImpl() => false;
		protected override bool IsPrimitiveImpl() => false;
		public override object InvokeMember(string name, BindingFlags invokeAttr, Binder binder, object target, object[] args, ParameterModifier[] modifiers, System.Globalization.CultureInfo culture, string[] namedParameters) => throw new NotSupportedException();
		public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException();
		public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException();
		public override bool IsDefined(Type attributeType, bool inherit) => throw new NotSupportedException();
	}

	// Placeholder for an unresolved array type over a domain type (e.g. MyClass[]).
	// Mirror of UnresolvedDomainType, but for arrays.
	internal class UnresolvedArrayType : Type
	{
		private readonly Type elementType;

		internal UnresolvedArrayType(Type elementType)
		{
			ArgumentNullException.ThrowIfNull(elementType);
			this.elementType = elementType;
		}

		internal Type ElementType => elementType;

		public override string Name => $"{elementType.Name}[]";
		public override string FullName => $"{elementType.FullName}[]";
		public override string Namespace => elementType.Namespace;
		public override Type BaseType => typeof(Array);
		public override Type UnderlyingSystemType => this;
		public override Assembly Assembly => throw new NotSupportedException("UnresolvedArrayType no tiene Assembly asociado hasta la fase de matching");
		public override Guid GUID => throw new NotSupportedException();
		public override Module Module => throw new NotSupportedException();
		public override string AssemblyQualifiedName => throw new NotSupportedException();

		protected override TypeAttributes GetAttributeFlagsImpl() => TypeAttributes.Public;
		protected override ConstructorInfo GetConstructorImpl(BindingFlags bindingAttr, Binder binder, CallingConventions callConvention, Type[] types, ParameterModifier[] modifiers) => throw new NotSupportedException();
		public override ConstructorInfo[] GetConstructors(BindingFlags bindingAttr) => throw new NotSupportedException();

		// IMPORTANT: IsArrayImpl returns true to indicate this is an array.
		protected override bool IsArrayImpl() => true;

		// GetElementType returns the array's element type.
		public override Type GetElementType() => elementType;

		protected override bool HasElementTypeImpl() => true;

		public override EventInfo GetEvent(string name, BindingFlags bindingAttr) => throw new NotSupportedException();
		public override EventInfo[] GetEvents(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override FieldInfo GetField(string name, BindingFlags bindingAttr) => throw new NotSupportedException();
		public override FieldInfo[] GetFields(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type GetInterface(string name, bool ignoreCase) => throw new NotSupportedException();
		public override Type[] GetInterfaces() => throw new NotSupportedException();
		public override MemberInfo[] GetMembers(BindingFlags bindingAttr) => throw new NotSupportedException();
		protected override MethodInfo GetMethodImpl(string name, BindingFlags bindingAttr, Binder binder, CallingConventions callConvention, Type[] types, ParameterModifier[] modifiers) => throw new NotSupportedException();
		public override MethodInfo[] GetMethods(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type GetNestedType(string name, BindingFlags bindingAttr) => throw new NotSupportedException();
		public override Type[] GetNestedTypes(BindingFlags bindingAttr) => throw new NotSupportedException();
		public override PropertyInfo[] GetProperties(BindingFlags bindingAttr) => throw new NotSupportedException();
		protected override PropertyInfo GetPropertyImpl(string name, BindingFlags bindingAttr, Binder binder, Type returnType, Type[] types, ParameterModifier[] modifiers) => throw new NotSupportedException();
		protected override bool IsByRefImpl() => false;
		protected override bool IsCOMObjectImpl() => false;
		protected override bool IsPointerImpl() => false;
		protected override bool IsPrimitiveImpl() => false;
		public override object InvokeMember(string name, BindingFlags invokeAttr, Binder binder, object target, object[] args, ParameterModifier[] modifiers, System.Globalization.CultureInfo culture, string[] namedParameters) => throw new NotSupportedException();
		public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException();
		public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException();
		public override bool IsDefined(Type attributeType, bool inherit) => throw new NotSupportedException();
	}

	// Base node for all patterns.
	internal abstract class PatternNode
	{
	}

	internal class TypedValuePlaceholder
	{
		internal Type ValueType { get; }
		internal string VariableName { get; }

		internal TypedValuePlaceholder(Type valueType, string variableName = null)
		{
			ArgumentNullException.ThrowIfNull(valueType);
			ValueType = valueType;
			VariableName = variableName;
		}
	}

	internal class OutParameterMarker
	{
		internal Type ValueType { get; }
		internal string ParameterName { get; }

		internal OutParameterMarker(Type valueType, string parameterName)
		{
			ArgumentNullException.ThrowIfNull(valueType);
			ArgumentNullException.ThrowIfNull(parameterName);
			ValueType = valueType;
			ParameterName = parameterName;
		}
	}

	// Root pattern that contains a list of expressions.
	internal class PatternListNode : PatternNode
	{
		internal List<ExpressionNode> Expressions { get; }

		// Script information captured for matching.
		private List<ScriptLiteral> scriptLiterals = new List<ScriptLiteral>();
		private List<ScriptIdentifier> scriptIdentifiers = new List<ScriptIdentifier>();
		private List<ScriptMemberAccess> scriptMemberAccesses = new List<ScriptMemberAccess>();
		private List<ScriptChainedAccess> scriptChainedAccesses = new List<ScriptChainedAccess>();
		private List<ScriptConstructorCall> scriptConstructorCalls = new List<ScriptConstructorCall>();
		private List<ScriptMethodCall> scriptMethodCalls = new List<ScriptMethodCall>();
		private List<ScriptAssignment> scriptAssignments = new List<ScriptAssignment>();

		// Plan 7 of the Tell primitive roadmap: tell-shaped journal entries the
		// matcher will compare against TellPatternNode / TellAckPatternNode.
		// Saga tells are intentionally NOT registered — saga matchers come with
		// Plan 8 of the roadmap.
		private List<ScriptTellStatement> scriptTellStatements = new List<ScriptTellStatement>();
		private List<ScriptTellAckStatement> scriptTellAckStatements = new List<ScriptTellAckStatement>();

		internal PatternListNode(List<ExpressionNode> expressions)
		{
			ArgumentNullException.ThrowIfNull(expressions);

			Expressions = expressions;
		}

		// Convenience methods used by Program to populate script information.
		internal void RegisterLiteral(object value, Type type, int position)
		{
			ArgumentNullException.ThrowIfNull(type);

			scriptLiterals.Add(new ScriptLiteral(value, type, position));
		}
		internal void RegisterIdentifier(string name, Type resolvedType, int position)
		{
			ArgumentNullException.ThrowIfNull(name);
			ArgumentNullException.ThrowIfNull(resolvedType);

			scriptIdentifiers.Add(new ScriptIdentifier(name, resolvedType, position));
		}

		internal void RegisterParameterIdentifier(string name, Type resolvedType, int position, Parameter parameterRef)
		{
			ArgumentNullException.ThrowIfNull(name);
			ArgumentNullException.ThrowIfNull(resolvedType);
			ArgumentNullException.ThrowIfNull(parameterRef);

			scriptIdentifiers.Add(new ScriptParameterIdentifier(name, resolvedType, position, parameterRef));
		}
		internal void RegisterMemberAccess(string targetName, string memberName, MemberInfo member, int position, Type receiverType = null)
		{
			ArgumentNullException.ThrowIfNull(memberName);
			ArgumentNullException.ThrowIfNull(member);

			scriptMemberAccesses.Add(new ScriptMemberAccess(targetName, memberName, member, position, receiverType));
		}
		internal void RegisterChainedAccess(List<MemberInfo> chain, int position)
		{
			ArgumentNullException.ThrowIfNull(chain);
			if (chain.Count == 0) throw new ArgumentException("The chain cannot be empty.", nameof(chain));

			scriptChainedAccesses.Add(new ScriptChainedAccess(chain, position));
		}
		internal void RegisterConstructorCall(Type type, List<Type> arguments, int position)
		{
			ArgumentNullException.ThrowIfNull(type);
			ArgumentNullException.ThrowIfNull(arguments);

			scriptConstructorCalls.Add(new ScriptConstructorCall(type, arguments, position));
		}
		internal void RegisterMethodCall(MethodInfo method, object target, List<object> arguments, int position, string targetName = null, Type receiverType = null)
		{
			ArgumentNullException.ThrowIfNull(method);

			scriptMethodCalls.Add(new ScriptMethodCall(method, target, arguments ?? new List<object>(), position, targetName, receiverType));
		}
		internal void RegisterAssignment(string targetName, Type targetType, object value, int position)
		{
			ArgumentNullException.ThrowIfNull(targetName);
			ArgumentNullException.ThrowIfNull(targetType);

			scriptAssignments.Add(new ScriptAssignment(targetName, targetType, value, position));
		}

		// Plan 7 of the Tell primitive roadmap: register an outbound tell sentence
		// from the script's parsed AST so the matcher can compare it against
		// TellPatternNode entries in the Reaction's pattern.
		internal void RegisterTellStatement(string targetClass, object targetIdValue, string commandName, object[] commandArgsValues, string envelopeId, int position)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(targetClass);
			ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
			ArgumentNullException.ThrowIfNull(commandArgsValues);

			scriptTellStatements.Add(new ScriptTellStatement(targetClass, targetIdValue, commandName, commandArgsValues, envelopeId, position));
		}

		internal void RegisterTellAckStatement(string ackId, string fromTargetClass, object fromTargetIdValue, int position)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(ackId);
			ArgumentException.ThrowIfNullOrWhiteSpace(fromTargetClass);

			scriptTellAckStatements.Add(new ScriptTellAckStatement(ackId, fromTargetClass, fromTargetIdValue, position));
		}

		// Accessors for the matching.
		internal IReadOnlyList<ScriptLiteral> ScriptLiterals => scriptLiterals;
		internal IReadOnlyList<ScriptIdentifier> ScriptIdentifiers => scriptIdentifiers;
		internal IReadOnlyList<ScriptMemberAccess> ScriptMemberAccesses => scriptMemberAccesses;
		internal IReadOnlyList<ScriptChainedAccess> ScriptChainedAccesses => scriptChainedAccesses;
		internal IReadOnlyList<ScriptConstructorCall> ScriptConstructorCalls => scriptConstructorCalls;
		internal IReadOnlyList<ScriptMethodCall> ScriptMethodCalls => scriptMethodCalls;
		internal IReadOnlyList<ScriptAssignment> ScriptAssignments => scriptAssignments;
		internal IReadOnlyList<ScriptTellStatement> ScriptTellStatements => scriptTellStatements;
		internal IReadOnlyList<ScriptTellAckStatement> ScriptTellAckStatements => scriptTellAckStatements;
		internal void ClearScriptInfo()
		{
			scriptLiterals.Clear();
			scriptIdentifiers.Clear();
			scriptMemberAccesses.Clear();
			scriptChainedAccesses.Clear();
			scriptConstructorCalls.Clear();
			scriptMethodCalls.Clear();
			scriptAssignments.Clear();
			scriptTellStatements.Clear();
			scriptTellAckStatements.Clear();
		}
	}

	// Structures that hold script information captured for matching.

	internal class ScriptLiteral
	{
		internal object Value { get; }
		internal Type Type { get; }
		internal int Position { get; }

		internal ScriptLiteral(object value, Type type, int position)
		{
			Type = type;
			Value = value;
			Position = position;
		}
	}

	internal class ScriptIdentifier
	{
		internal string Name { get; }
		internal Type ResolvedType { get; }
		internal int Position { get; }

		internal ScriptIdentifier(string name, Type resolvedType, int position)
		{
			Name = name;
			ResolvedType = resolvedType;
			Position = position;
		}
	}

	internal class ScriptParameterIdentifier : ScriptIdentifier
	{
		internal Parameter ParameterRef { get; }

		internal ScriptParameterIdentifier(string name, Type resolvedType, int position, Parameter parameterRef)
			: base(name, resolvedType, position)
		{
			ArgumentNullException.ThrowIfNull(parameterRef);
			ParameterRef = parameterRef;
		}
	}

	internal class ScriptMemberAccess
	{
		internal string TargetName { get; }
		internal string MemberName { get; }
		internal MemberInfo Member { get; }
		internal int Position { get; }
		// Static type of the receiver as the script typed it (may be a subtype of the
		// member's DeclaringType when the member is inherited from an abstract base). Lets
		// the matcher accept a receiver-type pattern written against the declared type, not
		// only against the type that declares the member. Null when the receiver is an
		// unnamed chained expression whose type was not resolved.
		internal Type ReceiverType { get; }

		internal ScriptMemberAccess(string targetName, string memberName, MemberInfo member, int position, Type receiverType = null)
		{
			TargetName = targetName;
			MemberName = memberName;
			Member = member;
			Position = position;
			ReceiverType = receiverType;
		}
	}

	internal class ScriptChainedAccess
	{
		internal List<MemberInfo> Chain { get; }
		internal int Position { get; }

		internal ScriptChainedAccess(List<MemberInfo> chain, int position)
		{
			Chain = chain;
			Position = position;
		}
	}

	internal class ScriptConstructorCall
	{
		internal Type Type { get; }
		internal List<Type> Arguments { get; }
		internal int Position { get; }

		internal ScriptConstructorCall(Type type, List<Type> arguments, int position)
		{
			Type = type;
			Arguments = arguments;
			Position = position;
		}
	}

	internal class ScriptMethodCall
	{
		internal MethodInfo Method { get; }
		internal object Target { get; }
		internal List<object> Arguments { get; }
		internal int Position { get; }
		internal string TargetName { get; }
		// Static type of the receiver as the script typed it (may be a subtype of the
		// method's DeclaringType when the method is inherited from an abstract base). Lets
		// the matcher accept a receiver-type pattern written against the declared type, not
		// only against the type that declares the method. Null when the receiver is an
		// unnamed chained expression whose type was not resolved.
		internal Type ReceiverType { get; }

		internal ScriptMethodCall(MethodInfo method, object target, List<object> arguments, int position, string targetName = null, Type receiverType = null)
		{
			Method = method;
			Target = target;
			Arguments = arguments;
			Position = position;
			TargetName = targetName;
			ReceiverType = receiverType;
		}
	}

	// Plan 7 of the Tell primitive roadmap: a tell sentence captured from the
	// script's parsed AST, evaluated to runtime values so the matcher can
	// compare it against a TellPatternNode without re-evaluating expressions.
	internal class ScriptTellStatement
	{
		internal string TargetClass { get; }
		internal object TargetIdValue { get; }       // null for broadcasts (TargetId expression was null)
		internal string CommandName { get; }
		internal object[] CommandArgsValues { get; }
		internal string EnvelopeId { get; }          // explicit IdLiteral or formatted hex of the implicit hash
		internal int Position { get; }

		internal ScriptTellStatement(string targetClass, object targetIdValue, string commandName, object[] commandArgsValues, string envelopeId, int position)
		{
			TargetClass = targetClass;
			TargetIdValue = targetIdValue;
			CommandName = commandName;
			CommandArgsValues = commandArgsValues;
			EnvelopeId = envelopeId;
			Position = position;
		}
	}

	// Plan 7 of the Tell primitive roadmap: an ack sentence captured from the
	// script's parsed AST, ready for comparison against a TellAckPatternNode.
	internal class ScriptTellAckStatement
	{
		internal string AckId { get; }
		internal string FromTargetClass { get; }
		internal object FromTargetIdValue { get; }
		internal int Position { get; }

		internal ScriptTellAckStatement(string ackId, string fromTargetClass, object fromTargetIdValue, int position)
		{
			AckId = ackId;
			FromTargetClass = fromTargetClass;
			FromTargetIdValue = fromTargetIdValue;
			Position = position;
		}
	}

	internal class ScriptAssignment
	{
		internal string TargetName { get; }
		internal Type TargetType { get; }
		internal object Value { get; }
		internal int Position { get; }

		internal ScriptAssignment(string targetName, Type targetType, object value, int position)
		{
			ArgumentNullException.ThrowIfNull(targetName);
			ArgumentNullException.ThrowIfNull(targetType);

			TargetName = targetName;
			TargetType = targetType;
			Value = value;
			Position = position;
		}
	}

	// Base node for expressions.
	internal abstract class ExpressionNode : PatternNode
	{
	}

	// Instance access: [instance:Type].Member
	internal class InstanceAccessNode : ExpressionNode
	{
		internal string InstanceName { get; }
		internal string TypeName { get; }
		internal MemberAccessNode MemberAccess { get; }

		internal InstanceAccessNode(string instanceName, string typeName, MemberAccessNode memberAccess)
		{
			ArgumentNullException.ThrowIfNull(typeName);

			InstanceName = instanceName; // may be null or "_"
			TypeName = typeName;
			MemberAccess = memberAccess; // may be null
		}
	}

	// Type access: [Type].Member
	internal class TypeAccessNode : ExpressionNode
	{
		internal string TypeName { get; }
		internal MemberAccessNode MemberAccess { get; }

		internal TypeAccessNode(string typeName, MemberAccessNode memberAccess)
		{
			ArgumentNullException.ThrowIfNull(typeName);

			TypeName = typeName;
			MemberAccess = memberAccess; // may be null
		}
	}

	// Constructor call: Class(...) or [Class](...)
	internal class ConstructorCallNode : ExpressionNode
	{
		internal string TypeName { get; }
		internal List<ParameterNode> Parameters { get; }

		internal ConstructorCallNode(string typeName, List<ParameterNode> parameters)
		{
			ArgumentNullException.ThrowIfNull(typeName);

			TypeName = typeName;
			Parameters = parameters ?? new List<ParameterNode>();
		}
	}

	// Assignment: $variable = expression;
	internal class AssignmentNode : ExpressionNode
	{
		internal string VariableName { get; }
		internal ExpressionNode Value { get; }

		internal AssignmentNode(string variableName, ExpressionNode value)
		{
			ArgumentNullException.ThrowIfNull(variableName);
			ArgumentNullException.ThrowIfNull(value);

			VariableName = variableName;
			Value = value;
		}
	}

	// Partial pattern: ... pattern1 ... pattern2 ...
	// Represents a sequence of patterns with wildcards (...) before, after, or between them.
	internal class PartialPatternNode : ExpressionNode
	{
		internal bool HasLeadingWildcard { get; }  // True when ... precedes the first pattern.
		internal List<ExpressionNode> Patterns { get; }  // List of patterns.
		internal bool HasTrailingWildcard { get; }  // True when ... follows the last pattern.

		internal PartialPatternNode(bool hasLeadingWildcard, List<ExpressionNode> patterns, bool hasTrailingWildcard)
		{
			ArgumentNullException.ThrowIfNull(patterns);
			if (patterns.Count == 0)
				throw new ArgumentException("The list of patterns cannot be empty.", nameof(patterns));

			HasLeadingWildcard = hasLeadingWildcard;
			Patterns = patterns;
			HasTrailingWildcard = hasTrailingWildcard;
		}
	}

	internal class WildcardExpressionNode : ExpressionNode
	{
		internal WildcardExpressionNode()
		{
		}
	}

	internal class LiteralExpressionNode : ExpressionNode
	{
		internal LiteralParameterNode Literal { get; }

		internal LiteralExpressionNode(LiteralParameterNode literal)
		{
			ArgumentNullException.ThrowIfNull(literal);
			Literal = literal;
		}
	}

	// Member access (property or method).
	internal class MemberAccessNode : PatternNode
	{
		internal string MemberName { get; }
		internal List<ParameterNode> Parameters { get; } // null when this is not a method.
		internal MemberAccessNode NextAccess { get; } // used for chaining.

		internal MemberAccessNode(string memberName, List<ParameterNode> parameters, MemberAccessNode nextAccess)
		{
			ArgumentNullException.ThrowIfNull(memberName);

			MemberName = memberName;
			Parameters = parameters; // null for properties.
			NextAccess = nextAccess; // null when there is no chaining.
		}
	}

	// Base node for parameters.
	internal abstract class ParameterNode : PatternNode
	{
	}

	// Wildcard: _
	internal class WildcardParameterNode : ParameterNode
	{
	}

	// Variable: $identifier
	internal class VariableParameterNode : ParameterNode
	{
		internal string VariableName { get; }

		internal VariableParameterNode(string variableName)
		{
			ArgumentNullException.ThrowIfNull(variableName);

			VariableName = variableName;
		}
	}

	// Literal
	internal class LiteralParameterNode : ParameterNode
	{
		internal object Value { get; }
		internal Type LiteralType { get; }
		internal Type ExplicitType { get; } // Type specified explicitly with ':'.

		internal LiteralParameterNode(object value, Type literalType, Type explicitType = null)
		{
			ArgumentNullException.ThrowIfNull(literalType);

			Value = value;
			LiteralType = literalType;
			ExplicitType = explicitType;
		}

		internal bool CompareLiteralsUsingInterpreter(object scriptValue)
		{
			if (Value == null || scriptValue == null) return false;

			try
			{
				// Build AST literals for both values.
				AstExpression patternLiteral = CreateLiteralExpression(Value);
				AstExpression scriptLiteral = CreateLiteralExpression(scriptValue);

				// Build the equality operation.
				var opIgualQue = new OpEqual(patternLiteral, scriptLiteral);

				// Run the comparison through the interpreter's logic.
				var result = opIgualQue.Execute();

				return result is bool boolResult && boolResult;
			}
			catch
			{
				// On failure, fall back to direct comparison.
				return Value.Equals(scriptValue);
			}
		}

		private AstExpression CreateLiteralExpression(object value)
		{
			if (value == null)
				return new LiteralNull();

			Type valueType = value.GetType();

			if (valueType == typeof(int))
				return new LiteralNumber((int)value);

			if (valueType == typeof(double))
				return new LiteralDouble((double)value);

			if (valueType == typeof(decimal))
				return new LiteralDecimal((decimal)value);

			if (valueType == typeof(string))
				return new LiteralString((string)value);

			if (valueType == typeof(bool))
				return (bool)value ? LiteralBoolean.LiteralTrue : LiteralBoolean.LiteralFalse;

			if (valueType == typeof(DateTime))
				return new LiteralDateTime((DateTime)value);

			// Fallback for unrecognized types.
			throw new InvalidOperationException($"Cannot create a literal for type '{valueType.Name}'.");
		}
	}

	// Typed parameter (identifier with an explicit type).
	internal class TypedParameterNode : ParameterNode
	{
		internal string ParameterName { get; } // may be null when only ':Type' is given.
		internal Type ParameterType { get; }

		internal TypedParameterNode(string parameterName, Type parameterType)
		{
			ArgumentNullException.ThrowIfNull(parameterType);

			ParameterName = parameterName;
			ParameterType = parameterType;
		}
	}

	// Argument that is itself a call-with-receiver: foo([_:Derived].goo($x)).
	// Wraps the call's ExpressionNode (InstanceAccessNode / TypeAccessNode).
	// The matcher matches it against the registered ScriptMethodCalls (not against the
	// argument's placeholder value, which is unknown because matching is static):
	// "such a call-with-receiver exists", capturing its inner $vars. It does not verify
	// the exact result->argument binding (sugar over a flat obligation).
	internal class NestedCallParameterNode : ParameterNode
	{
		internal ExpressionNode Call { get; }

		internal NestedCallParameterNode(ExpressionNode call)
		{
			ArgumentNullException.ThrowIfNull(call);

			Call = call;
		}
	}

	// Operators supported by guard clauses.
	internal enum GuardOperator
	{
		Equal,           // ==
		NotEqual,        // !=
		GreaterThan,     // >
		LessThan,        // <
		GreaterOrEqual,  // >=
		LessOrEqual,     // <=
		Contains,        // contains 'literal'
		NotContains      // not contains 'literal'
	}

	// A single 'where' clause: $variable op literal | not contains 'literal' | contains 'literal'.
	internal class GuardClause
	{
		internal string VariableName { get; }    // Name of the captured variable (null for not contains / contains).
		internal GuardOperator Operator { get; }
		internal object Value { get; }           // Literal value to compare against.
		internal Type ValueType { get; }

		internal GuardClause(string variableName, GuardOperator op, object value, Type valueType)
		{
			ArgumentNullException.ThrowIfNull(value);
			ArgumentNullException.ThrowIfNull(valueType);

			VariableName = variableName; // May be null for contains/not contains over the whole script.
			Operator = op;
			Value = value;
			ValueType = valueType;
		}
	}

	// Wrapper that attaches guard clauses ('where') to any expression.
	internal class GuardedExpressionNode : ExpressionNode
	{
		internal ExpressionNode InnerExpression { get; }
		internal List<GuardClause> Guards { get; }

		internal GuardedExpressionNode(ExpressionNode innerExpression, List<GuardClause> guards)
		{
			ArgumentNullException.ThrowIfNull(innerExpression);
			ArgumentNullException.ThrowIfNull(guards);
			if (guards.Count == 0)
				throw new ArgumentException("The guard list cannot be empty.", nameof(guards));

			InnerExpression = innerExpression;
			Guards = guards;
		}
	}

	// One branch of an OR alternative, with an optional label.
	internal class AlternativeBranch
	{
		internal ExpressionNode Expression { get; }
		internal string Label { get; } // 'as' label; may be null.

		internal AlternativeBranch(ExpressionNode expression, string label)
		{
			ArgumentNullException.ThrowIfNull(expression);
			Expression = expression;
			Label = label;
		}
	}

	// OR alternative: pattern1 | pattern2 [as 'label'] | pattern3 [as 'label']
	internal class AlternativeExpressionNode : ExpressionNode
	{
		internal List<AlternativeBranch> Branches { get; }

		internal AlternativeExpressionNode(List<AlternativeBranch> branches)
		{
			ArgumentNullException.ThrowIfNull(branches);
			if (branches.Count < 2)
				throw new ArgumentException("An alternative must have at least 2 branches.", nameof(branches));

			Branches = branches;
		}
	}

	// Expose: expose expression alias;
	// Represents a pattern that matches 'expose' statements in the script.
	// Examples:
	//   expose _:int total;      → match alias "total" with type int.
	//   expose 100 total;        → match alias "total" with literal value 100.
	//   expose _ total;          → match any value at alias "total".
	//   expose $x total;         → capture the alias "total" value into $x (Step 13).
	internal class ExposeNode : ExpressionNode
	{
		internal ParameterNode Expression { get; }
		internal string Alias { get; }

		internal ExposeNode(ParameterNode expression, string alias)
		{
			ArgumentNullException.ThrowIfNull(expression);
			ArgumentNullException.ThrowIfNull(alias);

			Expression = expression;
			Alias = alias;
		}
	}

	// Plan 7 of the Tell primitive roadmap: pattern node for outbound tell
	// statements in the journal. The DSL pattern syntax is:
	//
	//     tell <TargetClass>(<targetParam>) <CommandName>(<commandParams>) [id <idParam>]
	//
	// where each <param> can be a wildcard (_), a variable bind ($name), a
	// literal, or a typed variant. Plan 7 (b) does NOT match saga verbs
	// (start/step/compensate/close) or the through trailer — those are
	// recognised as part of Plan 8 (saga sub-family) and as future work.
	internal class TellPatternNode : ExpressionNode
	{
		internal string TargetClass { get; }
		internal ParameterNode TargetParameter { get; }
		internal string CommandName { get; }
		internal List<ParameterNode> CommandParameters { get; }
		internal ParameterNode IdParameter { get; } // null when the pattern omits the `id` trailer

		internal TellPatternNode(string targetClass, ParameterNode targetParameter, string commandName, List<ParameterNode> commandParameters, ParameterNode idParameter)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(targetClass);
			ArgumentNullException.ThrowIfNull(targetParameter);
			ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
			ArgumentNullException.ThrowIfNull(commandParameters);

			TargetClass = targetClass;
			TargetParameter = targetParameter;
			CommandName = commandName;
			CommandParameters = commandParameters;
			IdParameter = idParameter;
		}
	}

	// Plan 7 of the Tell primitive roadmap: pattern node for ack sentences in
	// the journal. The DSL pattern syntax is:
	//
	//     tell ack <ackIdParam> [from <FromTargetClass>(<fromTargetParam>)]
	//
	// where <ackIdParam> is typically a variable bind ($tid) so the OnSeek that
	// matched the originating tell can correlate against ThenSeek. The `from`
	// clause is optional in the pattern — if present it filters by sender.
	internal class TellAckPatternNode : ExpressionNode
	{
		internal ParameterNode AckIdParameter { get; }
		internal string FromTargetClass { get; }                  // null when the pattern omits `from <T>(...)`
		internal ParameterNode FromTargetParameter { get; }       // null when FromTargetClass is null

		internal TellAckPatternNode(ParameterNode ackIdParameter, string fromTargetClass, ParameterNode fromTargetParameter)
		{
			ArgumentNullException.ThrowIfNull(ackIdParameter);
			if (fromTargetClass != null && fromTargetParameter == null)
			{
				throw new ArgumentNullException(nameof(fromTargetParameter), "fromTargetParameter must be provided when fromTargetClass is given.");
			}
			if (fromTargetClass == null && fromTargetParameter != null)
			{
				throw new ArgumentNullException(nameof(fromTargetClass), "fromTargetClass must be provided when fromTargetParameter is given.");
			}

			AckIdParameter = ackIdParameter;
			FromTargetClass = fromTargetClass;
			FromTargetParameter = fromTargetParameter;
		}
	}
}
