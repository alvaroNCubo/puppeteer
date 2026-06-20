using System;

namespace Puppeteer.EventSourcing.Interpreter
{

	internal enum TokenType
	{
		nullToken,
		ELSE,
		EVAL,
		IF,
		FOR,
		IN,
		upgrade,
		tell,
		define,
		begin,
		end,
		lineComment,
		boolTrue,
		boolFalse,
		currency,
		date,
		time,
		@double,
		@decimal,
		number,
		assign,
		equality,
		inequality,
		logicalNot,
		logicalAnd,
		logicalOr,
		plus,
		minus,
		division,
		comma,
		dot,
		semicolon,
		rParen,
		lParen,
		colon,
		print,
		expose,
		stringLit,
		greaterThan,
		lessThan,
		greaterOrEqual,
		lessOrEqual,
		eof,
		multiplication,
		eol,
		notify,
		check,
		id,
		@as,
		variable,
		wildcard,
		lBracket,
		rBracket,
		ellipsis,
		question
	}

	internal readonly struct Token
	{
		private readonly TokenType type;
		private readonly int start;
		private readonly int end;

		internal Token(TokenType type, int start, int end)
		{
			if (type == TokenType.eof || type == TokenType.eol)
				throw new LanguageException($"TokenType '{type}' does not require start and end parameters. Use the constructor that only takes TokenType.");

			if (start < 0) throw new LanguageException($"Start '{start}' must be greater than or equal to zero.");
			if (end < start) throw new LanguageException($"End '{end}' must be greater than or equal to start '{start}'.");

			this.type = type;
			this.start = start;
			this.end = end;
		}

		internal Token(TokenType type)
		{
			switch (type)
			{
				case TokenType.eof:
				case TokenType.eol:
					this.type = type;
					this.start = Int32.MinValue;
					this.end = Int32.MinValue;
					break;
				default:
					throw new LanguageException($"TokenType '{type}' requires start and end parameters. Use a different constructor.");
			}
		}

		private bool HasPosition()
		{
			return start != Int32.MinValue && end != Int32.MinValue;
		}

		internal TokenType Type => type;

		// B.3.2: position accessors. LiteralExtractor uses these to substitute
		// literal token regions in a canonical script with parameter
		// references. start/end are inclusive character offsets in the
		// source the token was lexed from (Int32.MinValue if HasPosition()
		// is false, i.e. for eof/eol).
		internal int Start => start;
		internal int End => end;

		internal ReadOnlySpan<char> GetValor(ReadOnlySpan<char> input)
		{
			if (!HasPosition())
				return ReadOnlySpan<char>.Empty;

			if (start < 0 || end < start || end >= input.Length)
				throw new LanguageException($"Token position out of bounds. Start: {start}, End: {end}, Input Length: {input.Length}");

			int length = end - start + 1;

			ReadOnlySpan<char> result;
			if (type == TokenType.stringLit)
			{
				if (length > 2)
					result = input.Slice(start + 1, length - 2);
				else if (length == 2 && start + 1 > end - 1)
					result = ReadOnlySpan<char>.Empty;
				else
					throw new LanguageException($"Invalid literal form: string literal has only {length} character(s). A valid string literal must have at least length 2 (the two single quotes for an empty string).");
			}
			else
			{
				result = input.Slice(start, length);
			}
			return result;
		}

		internal ReadOnlySpan<char> GetValor(string input)
		{
			return this.GetValor(input.AsSpan());
		}

		public override string ToString()
		{
			if (!HasPosition())
				return $"Token Type: {type} (without position)";

			return $"Token Type: {type}, Start: {start}, End: {end}";
		}
	}

}
