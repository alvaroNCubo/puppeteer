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
			else if (string.Equals(name, "long", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(long);
			}
			// One branch used to answer for BOTH keywords and return decimal, so `(double)x` was not a
			// cast to double — it was the same expression as `(decimal)x`. A cast names a type; two type
			// names collapsed into one is the cast unable to say what it means. Splitting them also wakes
			// the `typeof(double)` execution branches in both engines, which had never run.
			else if (string.Equals(name, "decimal", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(decimal);
			}
			else if (string.Equals(name, "double", StringComparison.OrdinalIgnoreCase))
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
			else if (string.Equals(name, "char", StringComparison.OrdinalIgnoreCase))
			{
				result = typeof(char);
			}
			else if (string.Equals(name, "list", StringComparison.OrdinalIgnoreCase))
			{
				if (subType != null)
				{
					string strSubtype = subType.Name;
					Type elementType;
					if (string.Equals(strSubtype, "int", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(int);
					else if (string.Equals(strSubtype, "long", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(long);
					else if (string.Equals(strSubtype, "string", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(string);
					else if (string.Equals(strSubtype, "datetime", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(DateTime);
					else if (string.Equals(strSubtype, "bool", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(bool);
					else if (string.Equals(strSubtype, "double", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(double);
					// decimal was absent here, so `(list<decimal>)x` looked for a DOMAIN class named
					// 'decimal' and reported it missing from the libraries.
					else if (string.Equals(strSubtype, "decimal", StringComparison.OrdinalIgnoreCase))
						elementType = typeof(decimal);
					else
						elementType = libraries.GetTypeOrThrow(strSubtype);
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
				// This is the refusal an author actually reaches, since compatibility is checked before
				// anything executes. A cast to string is the one case with a specific remedy, so it
				// carries it here rather than in the execution branches, which validation now makes
				// unreachable for it.
				if (destinationType == typeof(string))
				{
					throw new LanguageException($"There is no cast from '{sourceType.Name}' to string: rendering a value as text depends on a format, and the format is a choice. Call ToString() on the value — ToString(format) where the type accepts one.");
				}
				if (sourceType == typeof(string) && destinationType == typeof(char))
				{
					throw new LanguageException("There is no cast from string to char: a string is a sequence and a char is one of its positions, so the position is named rather than converted. Write text[0].");
				}
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

			if (AstExpression.IsPromotableNumeric(nonNullableSource) && AstExpression.IsPromotableNumeric(nonNullableTarget))
				return true;

			if (nonNullableSource == typeof(DateTime) || nonNullableTarget == typeof(DateTime))
				return nonNullableSource == nonNullableTarget;

			if (nonNullableSource == typeof(bool) || nonNullableTarget == typeof(bool))
				return nonNullableSource == nonNullableTarget;

			// Enum: a string parses INTO an enum by name. The reverse is not a cast — see below.
			if (nonNullableTarget.IsEnum)
				return nonNullableSource == typeof(string) || nonNullableSource == nonNullableTarget;

			if (nonNullableSource.IsEnum)
				return nonNullableSource == nonNullableTarget;

			// char casts to and from char, and to nothing else. NOT from string: that direction
			// depends on the VALUE (a length-1 string bound, anything else failed), so the same script
			// was legal or not according to what the string held — one position of a sequence is
			// NAMED, not converted: text[0]. And NOT to string either, for the reason below.
			if (nonNullableTarget == typeof(char))
				return nonNullableSource == typeof(char);
			if (nonNullableSource == typeof(char))
				return nonNullableTarget == typeof(char);

			// NO cast produces a string. Rendering a value as text is an OPERATION and not a
			// conversion: it depends on a format, and the format is a choice — which is why C# has no
			// cast to string either, and why `+` in this language refuses to render a type it does not
			// know ("the textual form of such a value is a representation the author chooses",
			// OpAdd.ConcatenationRefused). A cast that renders contradicts that principle while
			// looking like it obeys it, so the author calls ToString(), naming the format where the
			// type accepts one. The identity cast stays: (string)text asserts a type rather than
			// converting anything, and that is how an argument opts out of being read as an enum
			// member.
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
            Type valueType = e.ComputeType();
            if (cast.IsEnum)
            {
                if (valueType == typeof(string))
                    return ParseEnumOrThrow(cast, (string)value);
                else if (valueType == cast)
                    return value;
                else
                    throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            if (cast == typeof(char))
            {
                if (valueType == typeof(char))
                    return value;
                // No string -> char, at ANY length. A cast whose legality depends on the VALUE is not
                // a cast: '(char)text' would be valid or not according to what text held at that
                // moment. A string is a sequence and a char is one of its positions, so the author
                // names the position — text[0] already yields a char.
                else if (valueType == typeof(string))
                    throw new LanguageException($"There is no cast from string to char. Take one position of it instead: text[0] yields a char.");
                else
                    throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            if (cast == typeof(String))
            {
                // Only the IDENTITY. Every other source used to be rendered here — int, long, double,
                // bool, DateTime, an enum, any object — while ExplicitCast REFUSED those same casts, so
                // validation said no and execution said yes about one expression. Removing them settles
                // the disagreement on the validation's side, which is also C#'s: text is produced by
                // ToString (naming the format where the type accepts one), never by a cast.
                if (valueType == typeof(string))
                    return value;

                throw new LanguageException($"There is no cast from '{valueType.Name}' to string: rendering a value as text depends on a format, and the format is a choice. Call ToString() on the value — ToString(format) where the type accepts one.");
            }
            else if (cast == typeof(int))
            {
				if (valueType == typeof(int))
					return value;
				else if (valueType == typeof(long))
					return (int)(long)value;
				else if (valueType == typeof(double))
					return (int)(double)value;
				else if (valueType == typeof(string))
				{
					double decimalValue;
					if (double.TryParse((string)value, out decimalValue))
						return (int)decimalValue;
					else
						throw new LanguageException($"Invalid cast from {valueType} to {cast}");
				}
				else if (valueType == typeof(bool))
					return Convert.ToInt32((bool)value);
				else if (valueType == typeof(object) && value != null)
					return Convert.ToInt32(value);
				else
					throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            else if (cast == typeof(long))
            {
				if (valueType == typeof(long))
					return value;
				else if (valueType == typeof(int))
					return (long)(int)value;
				else if (valueType == typeof(double))
					return (long)(double)value;
				else if (valueType == typeof(decimal))
					return (long)(decimal)value;
				else if (valueType == typeof(string))
				{
					long longValue;
					if (long.TryParse((string)value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out longValue))
						return longValue;
					else
						throw new LanguageException($"Invalid cast from {valueType} to {cast}");
				}
				else if (valueType == typeof(bool))
					return Convert.ToInt64((bool)value);
				else if (valueType == typeof(object) && value != null)
					return Convert.ToInt64(value);
				else
					throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            else if (cast == typeof(double))
            {
				if (valueType == typeof(double))
					return value;
				else if (valueType == typeof(int))
					return (double)(int)value;
				else if (valueType == typeof(long))
					return (double)(long)value;
				// The mirror of what the decimal group was missing. Both groups agreed in leaving the
				// other end of this pair out, so the two engines were equally incomplete and their
				// agreement hid it: only asking whether every admitted source is covered finds this.
				else if (valueType == typeof(decimal))
					return (double)(decimal)value;
				else if (valueType == typeof(string))
				{
					// Named for what it holds. The local was called `decimalValue` here too — the same
					// copy/paste fingerprint that left three defects in the decimal group.
					double parsed;
					if (double.TryParse((string)value, out parsed))
						return parsed;
					else
						throw new LanguageException($"Invalid cast from {valueType} to {cast}");
				}
				else if (valueType == typeof(bool))
					return Convert.ToDouble((bool)value);
				else if (valueType == typeof(object) && value != null)
					return Convert.ToDouble(value);
				else
					throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            else if (cast == typeof(decimal))
            {
                // Aligned with the compiled branch below, which is the correct one. Three of these
                // returned or omitted the wrong type, and the fingerprint of how says what happened:
                // the local is named `decimalValue` in the int and double groups above too, so this
                // block was written here, copied outward, and came back uncorrected in the two places
                // where the type actually mattered.
                //
                // The interpreted engine is legacy, but it earns its keep as the OTHER implementation
                // of the same semantics: a disagreement between the two is the alarm, and this session
                // found three real defects that way. That makes a divergence here not benign — it is
                // the instrument reading wrong, and no test could see it while both paths were free to
                // answer differently.
                if (valueType == typeof(decimal))
                    return value;
                else if (valueType == typeof(int))
                    return (decimal)(int)value;
                else if (valueType == typeof(long))
                    return Convert.ToDecimal((long)value);
                // Was missing entirely, which is what made `(decimal)someDouble` throw while the
                // compiled path converted it. It is also the signature the double <-> decimal axis of
                // the ambiguity matrix was waiting on.
                else if (valueType == typeof(double))
                    return (decimal)(double)value;
                else if (valueType == typeof(string))
                {
                    // Parsed AS a decimal, not as a double narrowed afterwards. Culture handling is
                    // left exactly as it was, matching the compiled decimal.Parse: changing it here
                    // would trade one divergence for another.
                    decimal parsed;
                    if (decimal.TryParse((string)value, out parsed))
                        return parsed;
                    else
                        throw new LanguageException($"Invalid cast from {valueType} to {cast}");
                }
                else if (valueType == typeof(bool))
                    return Convert.ToDecimal((bool)value);
				else if (valueType == typeof(object) && value != null)
					return Convert.ToDecimal(value);
				else
                    throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            else if (cast == typeof(DateTime))
            {
                if (valueType == typeof(DateTime))
                    return value;
                else
                    throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            else if (cast == typeof(bool))
            {
                if (valueType == typeof(bool))
                    return value;
                else if (valueType == typeof(string))
                {
                    bool valueOf;
                    if (System.Boolean.TryParse((string)value, out valueOf))
                        return valueOf;
                    else
                        throw new LanguageException($"Invalid cast from {valueType} to {cast}");
                }
                else if (valueType == typeof(int))
                    return (int)value != 0;
                else if (valueType == typeof(long))
                    return (long)value != 0;
                else if (valueType == typeof(double))
                    return (double)value != 0;
                else
                    throw new LanguageException($"Invalid cast from {valueType} to {cast}");
            }
            else if (cast == typeof(DateTime))
            {
                if (valueType == typeof(DateTime))
                    return value;
                else
                    throw new LanguageException($"Invalid cast from {valueType} to {cast}");
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
				var staticValue = Execute();
				return Expression.Constant(staticValue, ComputeType());
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

			if (targetType.IsEnum)
			{
				if (sourceType == typeof(string))
				{
					var parseMethod = typeof(AstExpression).GetMethod(
						nameof(AstExpression.ParseEnumOrThrow),
						System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
						null,
						new[] { typeof(Type), typeof(string) },
						null);
					return Expression.Convert(
						Expression.Call(parseMethod, Expression.Constant(targetType, typeof(Type)), expr),
						targetType);
				}
				else if (sourceType == targetType)
				{
					return expr;
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}

			if (targetType == typeof(char))
			{
				if (sourceType == typeof(char))
				{
					return expr;
				}
				else if (sourceType == typeof(string))
				{
					// Refused in both engines alike, so the two never disagree about which scripts are
					// legal — see the interpreted branch for why a value-dependent cast is not a cast.
					throw new LanguageException($"There is no cast from string to char. Take one position of it instead: text[0] yields a char.");
				}
				else
				{
					throw new LanguageException($"Invalid cast from {sourceType} to {targetType} in Expression.");
				}
			}
			else if (targetType == typeof(string))
			{
				// Mirrors the interpreted branch exactly: identity only. The two engines carried
				// separate rules for this cast, the compiled one ending in Convert.ToString, which
				// rendered anything at all.
				if (sourceType == typeof(string))
				{
					return expr;
				}

				throw new LanguageException($"There is no cast from '{sourceType.Name}' to string: rendering a value as text depends on a format, and the format is a choice. Call ToString() on the value — ToString(format) where the type accepts one.");
			}
			else if (targetType == typeof(int))
			{
				if (sourceType == typeof(int))
				{
					return expr;
				}
				else if (sourceType == typeof(long))
				{
					return Expression.Convert(expr, typeof(int));
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
			else if (targetType == typeof(long))
			{
				if (sourceType == typeof(long))
				{
					return expr;
				}
				else if (sourceType == typeof(int))
				{
					return Expression.Convert(expr, typeof(long));
				}
				else if (sourceType == typeof(double) || sourceType == typeof(decimal))
				{
					return Expression.Convert(expr, typeof(long));
				}
				else if (sourceType == typeof(string))
				{
					var parseMethod = typeof(long).GetMethod(nameof(Int64.Parse), new[] { typeof(string) });
					return Expression.Call(parseMethod, expr);
				}
				else if (sourceType == typeof(bool))
				{
					return Expression.Condition(expr, Expression.Constant(1L, typeof(long)), Expression.Constant(0L, typeof(long)));
				}
				else if (sourceType == typeof(object))
				{
					var toLongMethod = typeof(Convert).GetMethod(nameof(Convert.ToInt64), new[] { typeof(object) });
					return Expression.Call(toLongMethod, expr);
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
				else if (sourceType == typeof(long))
				{
					return Expression.Convert(expr, typeof(double));
				}
				else if (sourceType == typeof(decimal))
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
				else if (sourceType == typeof(long))
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
				else if (sourceType == typeof(long))
				{
					return Expression.NotEqual(expr, Expression.Constant(0L, typeof(long)));
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

        internal override void write(StringBuilder result, DatabaseType databaseType)
        {
            result.Append('(');
            id.write(result, databaseType);
            if (subType != null)
            {
                result.Append('<');
                subType.write(result, databaseType);
                result.Append('>');
            }
            result.Append(')');
            e.write(result, databaseType);
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
