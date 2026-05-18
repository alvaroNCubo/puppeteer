using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using Puppeteer.EventSourcing.Follower;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
    class OpCast : AstExpression
    {
        private readonly DomainLibraries libraries;
        private readonly AstExpression e;
        private readonly Id id;
        private readonly Id subType;

		internal OpCast(DomainLibraries libraries, Id id, AstExpression e)
        {
            this.libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
            this.e = e;
            this.id = id;
            if (String.Equals(id.Name.ToLower(),"list",StringComparison.OrdinalIgnoreCase)) throw new LanguageException("Cast target type must not be 'list' for the non-list cast constructor (use the constructor that takes a subType for list casts).");
        }

		internal OpCast(DomainLibraries libraries, Id id, AstExpression e, Id subType)
        {
            this.libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
            this.e = e;
            this.id = id;
            if (!String.Equals(id.Name, "list",StringComparison.OrdinalIgnoreCase)) throw new LanguageException("Cast target type must be 'list' when a subType is provided.");
            this.subType = subType;
        }

        internal override Type ComputeType()
        {
			string name = id.Name;
			Type result;
			if (string.Equals(name, "string", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(string);
			}
			else if (string.Equals(name, "int", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(int);
			}
			else if (string.Equals(name, "decimal", StringComparison.OrdinalIgnoreCase) ||
					 string.Equals(name, "double", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(decimal);
			}
			else if (string.Equals(name, "datetime", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(DateTime);
			}
			else if (string.Equals(name, "boolean", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(bool);
			}
			else if (string.Equals(name, "list", StringComparison.OrdinalIgnoreCase))
			{
				if (subType != null)
				{
					string strSubtipo = subType.Name;
					Type elementType;
					if (string.Equals(strSubtipo, "int", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(int);
					else if (string.Equals(strSubtipo, "string", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(string);
					else if (string.Equals(strSubtipo, "datetime", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(DateTime);
					else if (string.Equals(strSubtipo, "bool", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(bool);
					else if (string.Equals(strSubtipo, "double", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(double);
					else
						elementType = libraries.GetTypeOrThrow(strSubtipo);
					if (elementType == null) elementType = typeof(object);
					result = typeof(List<>).MakeGenericType(new[] { elementType });
				}
				else
				{
					result = typeof(List<>);
				}
			}
			else if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
			{
				throw new NotImplementedException();
			}
			else
			{
				result = libraries.GetTypeOrThrow(name);
			}
			return result;
        }

        private Type calcularTipoOfSubType()
        {
            if (subType == null) throw new LanguageException("The subType is only used for list element types and cannot be null.");
			string name = subType.Name;
			Type result;
			if (string.Equals(name, "string", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(String);
			}
			else if (string.Equals(name, "int", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(int);
			}
			else if (string.Equals(name, "decimal", StringComparison.OrdinalIgnoreCase) ||
					 string.Equals(name, "double", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(double);
			}
			else if (string.Equals(name, "datetime", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(DateTime);
			}
			else if (string.Equals(name, "boolean", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(bool);
			}
			else if (string.Equals(name, "list", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(List<>);
			}
			else if (string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
			{
				throw new NotImplementedException();
			}
			else
			{
				result = libraries.GetTypeOrThrow(name);
			}
            return result;
        }

        internal override void ValidateStatically()
        {
			var destinationType = ComputeType();
			if (destinationType == null)
            {
                throw new LanguageException($"Unknown class or type '{id.Name}' in cast expression.");
            }
            e.ValidateStatically();
			var sourceType = e.ComputeType();
			if (sourceType == null)
			{
				throw new LanguageException($"Cannot cast a value of unknown type to '{destinationType.Name}'.");
			}

			if (!ExplicitCast(sourceType, destinationType))
			{
				throw new LanguageException($"Cannot cast a value of type '{sourceType.Name}' to '{destinationType.Name}'.");
			}

			ForcedType = destinationType;
		}

		private static bool ExplicitCast(Type source, Type target)
		{
			if (source == target)
				return true;

			Type nonNullableSource = Nullable.GetUnderlyingType(source) ?? source;
			Type nonNullableTarget = Nullable.GetUnderlyingType(target) ?? target;

			if ((nonNullableSource == typeof(int) || nonNullableSource == typeof(double) || nonNullableSource == typeof(decimal)) &&
			(nonNullableTarget == typeof(int) || nonNullableTarget == typeof(double) || nonNullableTarget == typeof(decimal)))
				return true;

			if (nonNullableSource == typeof(DateTime) || nonNullableTarget == typeof(DateTime))
				return nonNullableSource == nonNullableTarget;

			if (nonNullableSource == typeof(bool) || nonNullableTarget == typeof(bool))
				return nonNullableSource == nonNullableTarget;

			if (nonNullableSource == typeof(string) || nonNullableTarget == typeof(string))
				return nonNullableSource == nonNullableTarget;

			// Lists: no explicit cast allowed (handled as implicit cast)
			if ((source.IsGenericType && source.GetGenericTypeDefinition() == typeof(List<>)) ||
				(target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>)) ||
				source.IsArray || target.IsArray)
			{
				return false;
			}

			// Casts between classes/interfaces if inheritance or interface implementation exists
			if (target.IsAssignableFrom(source) || source.IsAssignableFrom(target))
				return true;

			return false;
		}


		internal override object Execute()
        {
            object value = e.Execute();
            Type cast = ComputeType();
            Type tipoDelValor = e.ComputeType();
            if (cast == typeof(String))
            {
                if (tipoDelValor == typeof(string))
                    return value;
                else if (tipoDelValor == typeof(int))
                    return "" + (int)value;
                else if (tipoDelValor == typeof(double))
                    return "" + (double)value;
                else if (tipoDelValor == typeof(bool))
                    return "" + (bool)value;
                else if (tipoDelValor == typeof(DateTime))
                {
                    DateTime dateValor = (DateTime)value;
                    if (dateValor.Hour == 0 && dateValor.Minute == 0 && dateValor.Second == 0)
                    {
                        return dateValor.ToString("MM/dd/yyyy");
                    }
                    return dateValor.ToString("MM/dd/yyyy HH:mm:ss");
                }
                else
                    throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            else if (cast == typeof(int))
            {
				if (tipoDelValor == typeof(int))
					return value;
				else if (tipoDelValor == typeof(double))
					return (int)(double)value;
				else if (tipoDelValor == typeof(string))
				{
					double valorDecimal;
					if (double.TryParse((string)value, out valorDecimal))
						return (int)valorDecimal;
					else
						throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
				}
				else if (tipoDelValor == typeof(bool))
					return Convert.ToInt32((bool)value);
				else if (tipoDelValor == typeof(object) && value != null)
					return Convert.ToInt32(value);
				else
					throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            else if (cast == typeof(double))
            {
				if (tipoDelValor == typeof(double))
					return value;
				else if (tipoDelValor == typeof(int))
					return (double)(int)value;
				else if (tipoDelValor == typeof(string))
				{
					double valorDecimal;
					if (double.TryParse((string)value, out valorDecimal))
						return valorDecimal;
					else
						throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
				}
				else if (tipoDelValor == typeof(bool))
					return Convert.ToDouble((bool)value);
				else if (tipoDelValor == typeof(object) && value != null)
					return Convert.ToDouble(value);
				else
					throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            else if (cast == typeof(decimal))
            {
                if (tipoDelValor == typeof(decimal))
                    return value;
                else if (tipoDelValor == typeof(int))
                    return (double)(int)value;
                else if (tipoDelValor == typeof(string))
                {
                    double valorDecimal;
                    if (double.TryParse((string)value, out valorDecimal))
                        return valorDecimal;
                    else
                        throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
                }
                else if (tipoDelValor == typeof(bool))
                    return Convert.ToDecimal((bool)value);
				else if (tipoDelValor == typeof(object) && value != null)
					return Convert.ToDecimal(value);
				else
                    throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            else if (cast == typeof(DateTime))
            {
                if (tipoDelValor == typeof(DateTime))
                    return value;
                else
                    throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            else if (cast == typeof(bool))
            {
                if (tipoDelValor == typeof(bool))
                    return value;
                else if (tipoDelValor == typeof(string))
                {
                    bool valorDe;
                    if (System.Boolean.TryParse((string)value, out valorDe))
                        return valorDe;
                    else
                        throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
                }
                else if (tipoDelValor == typeof(int))
                    return (int)value != 0;
                else if (tipoDelValor == typeof(double))
                    return (double)value != 0;
                else
                    throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            else if (cast == typeof(DateTime))
            {
                if (tipoDelValor == typeof(DateTime))
                    return value;
                else
                    throw new LanguageException($"Invalid cast from {tipoDelValor} to {cast}");
            }
            // Fallback for class/interface casts: return the already-evaluated
            // value. Re-running e.Execute() here would invoke the inner
            // expression a second time, which silently corrupts state when
            // that expression has side effects (e.g. a stateful accumulator
            // whose internal dictionary was drained by the first call,
            // leaving it empty for the second).
            return value;
        }

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression expr = this.e.ExecuteExpression(parametersParam);

			if (expr is ConstantExpression)
			{
				var valorEstatico = Execute();
				return Expression.Constant(valorEstatico, ComputeType());
			}

			Type targetType = ComputeType();
			Type sourceType = e.ComputeType();

			// Helper method to get ToString with format for DateTime
			Expression FormatDateTime(Expression dateExpr)
			{
				var dtType = typeof(DateTime);
				var hourProp = Expression.Property(dateExpr, dtType.GetProperty(nameof(DateTime.Hour)));
				var minuteProp = Expression.Property(dateExpr, dtType.GetProperty(nameof(DateTime.Minute)));
				var secondProp = Expression.Property(dateExpr, dtType.GetProperty(nameof(DateTime.Second)));
				var zero = Expression.Constant(0, typeof(int));
				var formatShort = Expression.Constant("MM/dd/yyyy");
				var formatLong = Expression.Constant("MM/dd/yyyy HH:mm:ss");
				var toStringMethod = dtType.GetMethod("ToString", new[] { typeof(string) });

				// (date.Hour == 0 && date.Minute == 0 && date.Second == 0)
				var isShort = Expression.AndAlso(
					Expression.AndAlso(
						Expression.Equal(hourProp, zero),
						Expression.Equal(minuteProp, zero)
					),
					Expression.Equal(secondProp, zero)
				);

				// date.ToString("MM/dd/yyyy") : date.ToString("MM/dd/yyyy HH:mm:ss")
				return Expression.Condition(
					isShort,
					Expression.Call(dateExpr, toStringMethod, formatShort),
					Expression.Call(dateExpr, toStringMethod, formatLong)
				);
			}

			if (targetType == typeof(string))
			{
				if (sourceType == typeof(string))
				{
					return expr;
				}
				else if (sourceType == typeof(int) || sourceType == typeof(double) || sourceType == typeof(bool))
				{
					var toStringMethod = sourceType.GetMethod(nameof(String.ToString), Type.EmptyTypes);
					return Expression.Call(expr, toStringMethod);
				}
				else if (sourceType == typeof(DateTime))
				{
					return FormatDateTime(expr);
				}
				else if (sourceType == typeof(object))
				{
					var toStringMethod = typeof(Convert).GetMethod(nameof(Convert.ToString), new[] { typeof(object) });
					return Expression.Call(toStringMethod, expr);
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}
			else if (targetType == typeof(int))
			{
				if (sourceType == typeof(int))
				{
					return expr;
				}
				else if (sourceType == typeof(double))
				{
					return Expression.Convert(expr, typeof(int));
				}
				else if (sourceType == typeof(string))
				{
					var parseMethod = typeof(int).GetMethod(nameof(Int32.Parse), new[] { typeof(string) });
					return Expression.Call(parseMethod, expr);
				}
				else if (sourceType == typeof(bool))
				{
					return Expression.Condition(expr, Expression.Constant(1), Expression.Constant(0));
				}
				else if (sourceType == typeof(object))
				{
					var toIntMethod = typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) });
					return Expression.Call(toIntMethod, expr);
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}
			else if (targetType == typeof(double))
			{
				if (sourceType == typeof(double))
				{
					return expr;
				}
				else if (sourceType == typeof(int))
				{
					return Expression.Convert(expr, typeof(double));
				}
				else if (sourceType == typeof(string))
				{
					var parseMethod = typeof(double).GetMethod(nameof(Double.Parse), new[] { typeof(string) });
					return Expression.Call(parseMethod, expr);
				}
				else if (sourceType == typeof(bool))
				{
					var toDoubleMethod = typeof(Convert).GetMethod(nameof(Convert.ToDouble), new[] { typeof(bool) });
					return Expression.Call(toDoubleMethod, expr);
				}
				else if (sourceType == typeof(object))
				{
					var toDoubleMethod = typeof(Convert).GetMethod(nameof(Convert.ToDouble), new[] { typeof(object) });
					return Expression.Call(toDoubleMethod, expr);
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}
			else if (targetType == typeof(decimal))
			{
				if (sourceType == typeof(decimal))
				{
					return expr;
				}
				else if (sourceType == typeof(int))
				{
					return Expression.Convert(expr, typeof(decimal));
				}
				else if (sourceType == typeof(string))
				{
					var parseMethod = typeof(decimal).GetMethod(nameof(decimal.Parse), new[] { typeof(string) });
					return Expression.Call(parseMethod, expr);
				}
				else if (sourceType == typeof(bool))
				{
					var toDecimalMethod = typeof(Convert).GetMethod(nameof(Convert.ToDecimal), new[] { typeof(bool) });
					return Expression.Call(toDecimalMethod, expr);
				}
				else if (sourceType == typeof(double))
				{
					return Expression.Convert(expr, typeof(decimal));
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}
			else if (targetType == typeof(DateTime))
			{
				if (sourceType == typeof(DateTime))
				{
					return expr;
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}
			else if (targetType == typeof(bool))
			{
				if (sourceType == typeof(bool))
				{
					return expr;
				}
				else if (sourceType == typeof(string))
				{
					// bool.TryParse(string, out bool)
					var tryParseMethod = typeof(bool).GetMethod(nameof(bool.TryParse), new[] { typeof(string), typeof(bool).MakeByRefType() });
					var resultVar = Expression.Variable(typeof(bool), "result");
					var tryParseCall = Expression.Call(tryParseMethod, expr, resultVar);
					var exceptionCtor = typeof(LanguageException).GetConstructor(new[] { typeof(string) });
					var throwExpr = Expression.Throw(
						Expression.New(exceptionCtor, Expression.Constant($"Invalid cast from {sourceType} to {targetType}")),
						typeof(bool)
					);
					var block = Expression.Block(
						new[] { resultVar },
						Expression.IfThenElse(
							tryParseCall,
							Expression.Assign(resultVar, resultVar),
							throwExpr
						),
						resultVar
					);
					var parseMethod = typeof(bool).GetMethod(nameof(bool.Parse), new[] { typeof(string) });
					return Expression.Call(parseMethod, expr);
				}
				else if (sourceType == typeof(int))
				{
					return Expression.NotEqual(expr, Expression.Constant(0, typeof(int)));
				}
				else if (sourceType == typeof(double))
				{
					return Expression.NotEqual(expr, Expression.Constant(0.0, typeof(double)));
				}
				else if (sourceType == typeof(object))
				{
					var toBoolMethod = typeof(Convert).GetMethod(nameof(Convert.ToBoolean), new[] { typeof(object) });
					return Expression.Call(toBoolMethod, expr);
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}

			try
			{
				return Expression.Convert(expr, targetType);
			}
			catch (Exception)
			{
				throw new LanguageException($"Cannot cast from {sourceType} to {targetType} in Expression.");
			}
		}

        internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
        {
            e.PreparePatternMatching(patternAst, ref position);
            id.PreparePatternMatching(patternAst, ref position);
            if (subType != null)
            {
                subType.PreparePatternMatching(patternAst, ref position);
            }
        }

        internal override void write(StringBuilder resultado, DatabaseType databaseType)
        {
            resultado.Append('(');
            id.write(resultado, databaseType);
            if (subType != null)
            {
                resultado.Append('<');
                subType.write(resultado, databaseType);
                resultado.Append('>');
            }
            resultado.Append(')');
            e.write(resultado, databaseType);
        }

        internal override void Visit(ASTVisitor v)
        {
            if (this.GetType() == v.Target)
            {
                v.OnVisit(this);
            }
            id.Visit(v);
            if (subType != null) subType.Visit(v);
            e.Visit(v);
        }

    }
}
