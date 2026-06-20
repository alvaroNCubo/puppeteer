using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Puppeteer.EventSourcing.Interpreter
{
	class Lexer
	{
		private Input input;
		internal const int MAX_LEXEME_SIZE = 1024 * 64;
		internal Lexer()
		{
			string codigo = "\f";
			input = new Input(codigo);
			CurrentToken = new Token(TokenType.eof);
			Advance();
		}

		internal Token CurrentToken { get; private set; }

		internal ReadOnlySpan<char> CurrentLexeme()
		{
			return this.CurrentToken.GetValor(input.Script);
		}

		internal string Source
		{
			set
			{
				input.Script = value;
				Advance();
			}
		}

		private void Advance()
		{
			while (true)
			{
				try
				{
					SkipWhitespaceAndLoad();
					input.ResetForNextToken();
					if (IsDigit())
					{
						input.ConsumeChar();
						if (IsDigit()) //00..
						{
							input.ConsumeChar();
							if (input.CurrentChar == ':') //time HH:
							{
								input.ConsumeChar();
								if (IsDigit())
								{
									input.ConsumeChar();
									if (IsDigit())
									{
										input.ConsumeChar();
										if (input.CurrentChar == ':') //HH:MM:
										{
											input.ConsumeChar();
											if (IsDigit())
											{
												input.ConsumeChar();
												if (IsDigit())
												{
													input.ConsumeChar();
													if (IsEndOfNumber()) //HH:MM:SS<eol>
													{
														CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
														break;
													}

													input.Backtrack();
												}
												else if (IsEndOfNumber()) //HH:MM:S<eol>
												{
													CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
													break;
												}
												input.Backtrack();
											}
											input.Backtrack();
										}
										input.Backtrack();
									}
									else if (input.CurrentChar == ':') // HH:M:
									{
										input.ConsumeChar();
										if (IsDigit()) //HH:M:S
										{
											input.ConsumeChar();
											if (IsDigit()) //HH:M:SS
											{
												input.ConsumeChar();
												if (IsEndOfNumber())
												{
													CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
													break;
												}
											}
											else if (IsEndOfNumber()) //HH:M:S<eol>
											{
												CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
												break;
											}
										}
										input.Backtrack();
									}
									input.Backtrack();
								}
								input.Backtrack();
							}
							else if (IsSlash()) // MM/
							{
								input.ConsumeChar();
								if (IsDigit())
								{
									input.ConsumeChar();
									if (IsDigit())
									{
										input.ConsumeChar();
										if (IsSlash()) //MM/DD/
										{
											input.ConsumeChar();
											if (IsDigit())
											{
												input.ConsumeChar();
												if (IsDigit())
												{
													input.ConsumeChar();
													if (IsDigit())
													{
														input.ConsumeChar();
														if (IsDigit())
														{
															input.ConsumeChar();
															if (IsEndOfNumber())
															{
																CurrentToken = new Token(TokenType.date, input.LexemeStart, input.LexemeEnd);
																break;
															}
															input.Backtrack();
														}
														input.Backtrack();
													}
													input.Backtrack();
												}
												input.Backtrack();
											}
											input.Backtrack();
										}
										input.Backtrack();
									}
									else if (IsSlash()) //MM/D/
									{
										input.ConsumeChar();
										if (IsDigit())
										{
											input.ConsumeChar();
											if (IsDigit())
											{
												input.ConsumeChar();
												if (IsDigit())
												{
													input.ConsumeChar();
													if (IsDigit())
													{
														input.ConsumeChar();
														if (IsEndOfNumber())
														{
															CurrentToken = new Token(TokenType.date, input.LexemeStart, input.LexemeEnd);
															break;
														}
														input.Backtrack();
													}
													input.Backtrack();
												}
												input.Backtrack();
											}
											input.Backtrack();
										}
										input.Backtrack();
									}
									input.Backtrack();
								}
								input.Backtrack();
							}
							input.Backtrack();
						}
						else if (IsSlash()) //0...
						{
							input.ConsumeChar();
							if (IsDigit())
							{
								input.ConsumeChar();
								if (IsDigit()) //M/DD
								{
									input.ConsumeChar();
									if (IsSlash())
									{
										input.ConsumeChar();
										if (IsDigit())
										{
											input.ConsumeChar();
											if (IsDigit())
											{
												input.ConsumeChar();
												if (IsDigit())
												{
													input.ConsumeChar();
													if (IsDigit())
													{
														input.ConsumeChar();
														if (IsEndOfNumber())
														{
															CurrentToken = new Token(TokenType.date, input.LexemeStart, input.LexemeEnd);
															break;
														}
														input.Backtrack();
													}
													input.Backtrack();
												}
												input.Backtrack();
											}
											input.Backtrack();
										}
										input.Backtrack();
									}
									input.Backtrack();
								}
								else if (IsSlash()) //M/D/..
								{
									input.ConsumeChar();
									if (IsDigit())
									{
										input.ConsumeChar();
										if (IsDigit())
										{
											input.ConsumeChar();
											if (IsDigit())
											{
												input.ConsumeChar();
												if (IsDigit())
												{
													input.ConsumeChar();
													if (IsEndOfNumber())
													{
														CurrentToken = new Token(TokenType.date, input.LexemeStart, input.LexemeEnd);
														break;
													}
													input.Backtrack();
												}
												input.Backtrack();
											}
											input.Backtrack();
										}
										input.Backtrack();
									}
									input.Backtrack();
								}
								input.Backtrack();
							}
							input.Backtrack();
						}
						else if (input.CurrentChar == ':') // H:
						{
							input.ConsumeChar();
							if (IsDigit())
							{
								input.ConsumeChar();
								if (IsDigit()) //H:MM
								{
									input.ConsumeChar();
									if (input.CurrentChar == ':') //H:MM:
									{
										input.ConsumeChar();
										if (IsDigit()) //H:MM:S
										{
											input.ConsumeChar();
											if (IsDigit()) //H:MM:SS
											{
												input.ConsumeChar();
												if (IsEndOfNumber())
												{
													CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
													break;
												}
											}
											else if (IsEndOfNumber()) //H:MM:S
											{
												CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
												break;
											}
										}
										input.Backtrack();
									}
									input.Backtrack();
								}
								else if (input.CurrentChar == ':') //H:N:
								{
									input.ConsumeChar();
									if (IsDigit()) //H:M:S
									{
										input.ConsumeChar();
										if (IsDigit()) //H:MM:SS
										{
											input.ConsumeChar();
											if (IsEndOfNumber())
											{
												CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
												break;
											}
										}
										else if (IsEndOfNumber()) //H:M:S
										{
											CurrentToken = new Token(TokenType.time, input.LexemeStart, input.LexemeEnd);
											break;
										}
									}
									input.Backtrack();
								}
							}
							input.Backtrack();
						}

						CurrentToken = ProcessNumber();
						break;
					}
					else if (input.CurrentChar == '$')
					{
						input.SkipChar();
						if (IsIdentifierChar())
						{
							ProcessIdentifier();
							CurrentToken = new Token(TokenType.variable, input.LexemeStart, input.LexemeEnd);
							return;
						}
						throw new LanguageException($"Expected an identifier after '$' at line {input.Row}, column {input.Column}, but found '{input.CurrentChar}'.", input.CurrentChar + "", input.Row, input.Column);
					}
					else if (IsIdentifierChar())
					{
						if (input.CurrentChar == '_')
						{
							input.ConsumeChar();
							if (!IsIdentifierChar() && !IsDigit())
							{
								CurrentToken = new Token(TokenType.wildcard, input.LexemeStart, input.LexemeEnd);
								return;
							}
						}

						ProcessIdentifier();

						var cadenaActualOriginal = input.CurrentString();

						if (cadenaActualOriginal.Equals("PRINT".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.print, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("EXPOSE".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.expose, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("AS".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.@as, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("TRUE".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.boolTrue, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("FALSE".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.boolFalse, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("IF".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.IF, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("ELSE".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.ELSE, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("EVAL".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.EVAL, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("NULL".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.nullToken, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("FOR".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.FOR, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("IN".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.IN, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("CHECK".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.check, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("NOTIFY".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.notify, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("UPGRADE".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.upgrade, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("TELL".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							CurrentToken = new Token(TokenType.tell, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else if (cadenaActualOriginal.Equals("DEFINE".AsSpan(), StringComparison.OrdinalIgnoreCase))
						{
							// Phase 1 of the Action refactor (project_puppeteer_action_refactor_plan.md):
							// 'define' is the statement-level keyword for `define action <id> (params) as
							// <body> end;`. Promoted to a formal TokenType to mirror 'upgrade' and 'tell'.
							// 'action' and 'end' stay as contextual keywords recognised by the parser when
							// it sees TokenType.id with the matching lexeme — same pattern as the saga
							// verbs in the Tell roadmap (start/step/compensate/close).
							CurrentToken = new Token(TokenType.define, input.LexemeStart, input.LexemeEnd);
							return;
						}
						else
						{
							CurrentToken = new Token(TokenType.id, input.LexemeStart, input.LexemeEnd);
							return;
						}
					}
					else
					{
						switch (input.CurrentChar)
						{
							case '\'':
								ProcessStringLiteral();
								CurrentToken = new Token(TokenType.stringLit, input.LexemeStart, input.LexemeEnd);
								return;
							case '.':
								input.ConsumeChar();
								if (input.CurrentChar == '.')
								{
									input.ConsumeChar();
									if (input.CurrentChar == '.')
									{
										input.ConsumeChar();
										CurrentToken = new Token(TokenType.ellipsis, input.LexemeStart, input.LexemeEnd);
										return;
									}
									input.Backtrack();
								}
								CurrentToken = new Token(TokenType.dot, input.LexemeStart, input.LexemeEnd);
								return;
							case '+':
								input.SkipChar();
								CurrentToken = new Token(TokenType.plus, input.LexemeStart, input.LexemeEnd);
								return;
							case '-':
								input.SkipChar();
								CurrentToken = new Token(TokenType.minus, input.LexemeStart, input.LexemeEnd);
								return;
							case '/':
								input.ConsumeChar();
								bool esComentarioDeLinea = input.CurrentChar == '/';
								bool esComentarioDeBloque = input.CurrentChar == '*';
								if (esComentarioDeLinea)
								{
									input.ConsumeChar();
									ProcessLineComment();
									continue;
								}
								else if (esComentarioDeBloque)
								{
									input.ConsumeChar();
									ProcessBlockComment();
									continue;
								}
								else
								{
									CurrentToken = new Token(TokenType.division, input.LexemeStart, input.LexemeEnd);
								}
								return;
							case '>':
								input.SkipChar();
								if (input.CurrentChar == '=')
								{
									input.SkipChar();
									CurrentToken = new Token(TokenType.greaterOrEqual, input.LexemeStart, input.LexemeEnd);
									return;
								}
								CurrentToken = new Token(TokenType.greaterThan, input.LexemeStart, input.LexemeEnd);
								return;
							case '<':
								input.SkipChar();
								if (input.CurrentChar == '=')
								{
									input.SkipChar();
									CurrentToken = new Token(TokenType.lessOrEqual, input.LexemeStart, input.LexemeEnd);
									return;
								}

								CurrentToken = new Token(TokenType.lessThan, input.LexemeStart, input.LexemeEnd);
								return;
							case '=':
								input.SkipChar();
								if (input.CurrentChar == '=')
								{
									input.SkipChar();
									CurrentToken = new Token(TokenType.equality, input.LexemeStart, input.LexemeEnd);
									return;
								}

								CurrentToken = new Token(TokenType.assign, input.LexemeStart, input.LexemeEnd);
								return;
							case '&':
								input.SkipChar();
								if (input.CurrentChar == '&')
								{
									input.SkipChar();
									CurrentToken = new Token(TokenType.logicalAnd, input.LexemeStart, input.LexemeEnd);
									return;
								}
								throw new LanguageException($"Syntax error near '&' at line {input.Row}, column {input.Column}: expected '&&' for logical AND, but found '{input.CurrentChar}'.", input.CurrentChar + "", input.Row, input.Column);
							case '|':
								input.SkipChar();
								if (input.CurrentChar == '|')
								{
									input.SkipChar();
									CurrentToken = new Token(TokenType.logicalOr, input.LexemeStart, input.LexemeEnd);
									return;
								}
								throw new LanguageException($"Syntax error near '|' at line {input.Row}, column {input.Column}: expected '||' for logical OR, but found '{input.CurrentChar}'.", input.CurrentChar + "", input.Row, input.Column);
							case '!':
								input.SkipChar();
								if (input.CurrentChar == '=')
								{
									input.SkipChar();
									CurrentToken = new Token(TokenType.inequality, input.LexemeStart, input.LexemeEnd);
									return;
								}
								CurrentToken = new Token(TokenType.logicalNot, input.LexemeStart, input.LexemeEnd);
								return;
							case '*':
								input.ConsumeChar();
								CurrentToken = new Token(TokenType.multiplication, input.LexemeStart, input.LexemeEnd);
								return;
							case '}':
								input.SkipChar();
								CurrentToken = new Token(TokenType.end, input.LexemeStart, input.LexemeEnd);
								return;
							case '{':
								input.SkipChar();
								CurrentToken = new Token(TokenType.begin, input.LexemeStart, input.LexemeEnd);
								return;
							case '(':
								input.SkipChar();
								CurrentToken = new Token(TokenType.lParen, input.LexemeStart, input.LexemeEnd);
								return;
							case ')':
								input.SkipChar();
								CurrentToken = new Token(TokenType.rParen, input.LexemeStart, input.LexemeEnd);
								return;
							case ':':
								input.SkipChar();
								CurrentToken = new Token(TokenType.colon, input.LexemeStart, input.LexemeEnd);
								return;
							case ',':
								input.SkipChar();
								CurrentToken = new Token(TokenType.comma, input.LexemeStart, input.LexemeEnd);
								return;
							case ';':
								input.SkipChar();
								CurrentToken = new Token(TokenType.semicolon, input.LexemeStart, input.LexemeEnd);
								return;
							case '[':
								input.SkipChar();
								CurrentToken = new Token(TokenType.lBracket, input.LexemeStart, input.LexemeEnd);
								return;
							case ']':
								input.SkipChar();
								CurrentToken = new Token(TokenType.rBracket, input.LexemeStart, input.LexemeEnd);
								return;
							case '?':
								input.SkipChar();
								CurrentToken = new Token(TokenType.question, input.LexemeStart, input.LexemeEnd);
								return;
							case '\f':
								CurrentToken = new Token(TokenType.eof);
								return;
							case '\n':
								input.SkipChar();
								continue;
							case '\r':
								input.SkipChar();
								if (input.CurrentChar == '\n')
								{
									input.SkipChar();
								}
								continue;
						}
					}
				}
				catch (LanguageException)
				{
					throw;
				}
				catch (Exception)
				{
					throw new LanguageException($"Invalid character '{input.CurrentChar}' at line {input.Row}, column {input.Column}.", input.CurrentString().ToString(), input.Row, input.Column);
				}

				throw new LanguageException($"The line contains invalid characters at line {input.Row}, column {input.Column}.", input.CurrentString().ToString(), input.Row, input.Column);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void ProcessLineComment()
		{
			while (!IsEndOfStatement())
			{
				input.ConsumeChar();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void ProcessBlockComment()
		{
			while (true)
			{
				if (input.CurrentChar == '\f')
				{
					throw new LanguageException("EOF inesperado en comentario de bloque", input.CurrentString().ToString(), input.Row, input.Column);
				}
				if (input.CurrentChar == '*')
				{
					input.ConsumeChar();
					if (input.CurrentChar == '/')
					{
						input.ConsumeChar();
						break;
					}
				}
				else
				{
					input.ConsumeChar();
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void Accept()
		{
			Advance();
		}

		internal void Accept(TokenType type)
		{
			TokenType currentType = CurrentToken.Type;
			if (currentType != type)
			{
				throw new LanguageException($"Expected token type '{type}' at line {input.Row}, column {input.Column}, but found value '{CurrentToken.GetValor(input.Script)}' of type '{currentType}'.", input.CurrentString().ToString(), input.Row, input.Column);
			}
			Accept();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private Token ProcessNumber()
		{
			Token result;
			bool esDecimal = false;
			while (IsDigit() || IsDot())
			{

				if (IsDot())
				{
					if (esDecimal)
					{
						throw new LanguageException($"More than one decimal point found in numeric literal at line {input.Row}, column {input.Column}.", input.CurrentString().ToString(), input.Row, input.Column);
					}
					else
					{
						esDecimal = true;
					}
				}
				input.ConsumeChar();

			}
			// Verificar sufijo decimal 'm' o 'M'
			if (IsDecimalSuffix())
			{
				input.SkipChar();
				result = new Token(TokenType.@decimal, input.LexemeStart, input.LexemeEnd);
			}
			// Verificar sufijo double 'd' o 'D'
			else if (IsDoubleSuffix())
			{
				input.SkipChar();
				result = new Token(TokenType.@double, input.LexemeStart, input.LexemeEnd);
			}
			else if (esDecimal)
			{
				result = new Token(TokenType.@double, input.LexemeStart, input.LexemeEnd);
			}
			else
			{
				result = new Token(TokenType.number, input.LexemeStart, input.LexemeEnd);
			}

			return result;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void ProcessIdentifier()
		{
			while (IsIdentifierChar() || IsDigit())
			{
				input.ConsumeChar();
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void ProcessStringLiteral()
		{
			char comillaInicial = input.CurrentChar;
			input.ConsumeChar();
			while (true)
			{
				if (input.CurrentChar == comillaInicial)
				{
					input.ConsumeChar();
					break;
				}
				else if (input.CurrentChar == '\\')
				{
					// Check for escaped quote or backslash
					input.ConsumeChar();
					if (input.CurrentChar == '\'' || input.CurrentChar == '\\')
					{
						input.ConsumeChar();
					}
				}
				else if (input.CurrentChar == '\f')
				{
					throw new LanguageException("EOF inesperado en literal de str", "", input.Row, input.Column);
				}
				else
				{
					input.ConsumeChar();
				}
			}
		}

		private static readonly string CARACTERES_VALIDOS = new string(new char[] { '"', ';', '=', ':', ',', '(', ')', '+', '\'', '/', '*', '-', '>', '<', '!', '{', '}', '%', '.', '&', '|', '[', ']', '$', '_', '?' });

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void SkipWhitespaceAndLoad()
		{
			while (true)
			{
				bool esFinDeArchivo_o_noEsUnEspacio = char.IsLetterOrDigit(input.CurrentChar) || CARACTERES_VALIDOS.IndexOf(input.CurrentChar) >= 0 || input.CurrentChar == '\f';
				if (esFinDeArchivo_o_noEsUnEspacio)
				{
					break;
				}
				else
				{
					input.SetInicioLexemaToIndiceProximoChar();
					input.SkipChar();
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsSlash()
		{
			bool esUnDividir = input.CurrentChar == '/';
			return esUnDividir;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsDot()
		{
			bool esUnPunto = input.CurrentChar == '.';
			return esUnPunto;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsDigit()
		{
			bool esUnNumero = char.IsDigit(input.CurrentChar);
			return esUnNumero;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal bool IsWhitespace()
		{
			bool esUnEspacio = input.CurrentChar == ' ' || input.CurrentChar == '\t';
			return esUnEspacio;
		}

		private static readonly string OPERADORES = new string(new char[] { '=', '+', '-', '*', '<', '>', '!', '/' });
		private static readonly string FIN_DE_NUMERO = new string(new char[] { ',', ')', '}', ';', 'm', 'M', 'd', 'D' });

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsEndOfNumber()
		{
			bool esUnFinalDeNumero = IsWhitespace() || OPERADORES.IndexOf(input.CurrentChar) >= 0 || FIN_DE_NUMERO.IndexOf(input.CurrentChar) >= 0 || IsEndOfStatement();
			return esUnFinalDeNumero;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsEndOfStatement()
		{
			bool esElFinalDelComando = input.CurrentChar == '\n' || input.CurrentChar == '\r' || input.CurrentChar == '\f';
			return esElFinalDelComando;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsDecimalSuffix()
		{
			bool esSufijoDecimal = input.CurrentChar == 'm' || input.CurrentChar == 'M';
			return esSufijoDecimal;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsDoubleSuffix()
		{
			bool esSufijoDouble = input.CurrentChar == 'd' || input.CurrentChar == 'D';
			return esSufijoDouble;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool IsIdentifierChar()
		{
			char character = input.CurrentChar;
			bool esLetra = char.IsLetter(character);
			if (esLetra)
			{
				return true;
			}
			bool esGuionBajo = character == '_';
			bool esNumeral = character == '#';
			bool esArroba = character == '@';
			return esGuionBajo || esNumeral || esArroba;
		}

		internal int Row()
		{
			return input.Row;
		}

		internal int Column()
		{
			return input.Column;
		}

		private struct Input
		{
			private string script;
			private int indiceProximoChar;

			private Positions positions;

			internal int LexemeStart { get; private set; }
			internal int LexemeEnd { get; private set; }

			internal Input(string script)
			{
				Row = 1;
				Column = 0;

				indiceProximoChar = 0;
				positions = new Positions(32);

				this.script = script;
				CurrentChar = script.Length > 0 ? this.script[0] : '\t';


				positions.SavePosition(Row, Column, indiceProximoChar);

				LexemeStart = 0;
				LexemeEnd = 0;
			}

			internal string Script
			{
				set
				{
					Row = 1;
					Column = 0;

					indiceProximoChar = 0;
					positions.ResetForNextToken();

					this.script = value;
					CurrentChar = script.Length > 0 ? this.script[0] : '\t';

					positions.SavePosition(Row, Column, indiceProximoChar);

					LexemeStart = 0;
					LexemeEnd = 0;
				}
				get
				{
					return this.script;
				}
			}


			internal char CurrentChar { get; private set; }

			internal int Row { get; private set; }

			internal int Column { get; private set; }

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void ResetForNextToken()
			{
				LexemeStart = indiceProximoChar;
				LexemeEnd = indiceProximoChar;
				positions.ResetForNextToken();
				positions.SavePosition(Row, Column, indiceProximoChar);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void RecordLineBreakAndCapture()
			{
				Row++;
				Column = 0;
				AdvanceCursor();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void RecordLineBreakNoCapture()
			{
				Row++;
				Column = 0;
				AdvanceCursorNoCapture();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void RecordColumnAndCapture()
			{
				Column++;
				AdvanceCursor();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void RecordColumnNoCapture()
			{
				Column++;
				AdvanceCursorNoCapture();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void AdvanceCursor()
			{
				positions.SavePosition(Row, Column, indiceProximoChar);

				if (indiceProximoChar < script.Length)
				{
					LexemeEnd = indiceProximoChar;
					indiceProximoChar++;
					CurrentChar = indiceProximoChar >= script.Length ? '\f' : script[indiceProximoChar];
				}
				else
				{
					CurrentChar = '\f';
					LexemeEnd = indiceProximoChar;
				}
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void AdvanceCursorNoCapture()
			{
				if (indiceProximoChar < script.Length)
				{
					// Removed the modification of LexemeStart and LexemeEnd here.
					indiceProximoChar++;
					CurrentChar = indiceProximoChar >= script.Length ? '\f' : script[indiceProximoChar];
				}
				else
				{
					CurrentChar = '\f';
					// Removed the modification of LexemeStart and LexemeEnd here
				}
			}

			internal void ConsumeChar()
			{
				switch (CurrentChar)
				{
					case '\n':
						RecordLineBreakAndCapture();
						break;
					case '\f':
						throw new LanguageException("Unexpected EOF.", CurrentString().ToString(), Row, Column);
					default:
						RecordColumnAndCapture();
						break;
				}
			}

			internal void SkipChar()
			{
				switch (CurrentChar)
				{
					case '\n':
					case '\r':
						RecordLineBreakNoCapture();
						break;
					case '\f':
						throw new LanguageException("Unexpected EOF.", CurrentString().ToString(), Row, Column);
					case '\t':
					default:
						RecordColumnNoCapture();
						break;
				}
			}

			internal void Backtrack()
			{
				indiceProximoChar = positions.IndiceActual;
				Column = positions.Column;
				Row = positions.Row;
				positions.RemoveLastPosition();
				LexemeEnd = indiceProximoChar - 1;
				if (indiceProximoChar < script.Length && indiceProximoChar > 0)
				{
					CurrentChar = script[indiceProximoChar];
				}
				else
				{
					CurrentChar = '\f';
				}
			}

			internal ReadOnlySpan<char> CurrentString()
			{
				if (LexemeEnd >= LexemeStart && LexemeStart >= 0 && LexemeEnd <= script.Length)
				{
					var ultimoIndiceNoBlanco = LexemeEnd;
					while (ultimoIndiceNoBlanco < script.Length && ultimoIndiceNoBlanco >= LexemeStart && char.IsWhiteSpace(script[ultimoIndiceNoBlanco]))
					{
						ultimoIndiceNoBlanco--;
					}

					var length = ultimoIndiceNoBlanco - LexemeStart + 1;
					if (length > 0 && LexemeStart + length <= script.Length)
					{
						// AsSpan sobre el script en vez de Substring: CurrentString() se invoca
						// para cada token identificador (matcheo de keywords en Advance), asi que
						// el Substring alocaba un string transitorio por identificador. Sobre un
						// journal de millones de tokens eso es presion de GC que golpea todas las
						// etapas del pipeline de rehidratacion. El span apunta directo al script.
						return script.AsSpan(LexemeStart, length);
					}
				}

				return ReadOnlySpan<char>.Empty;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void SetInicioLexemaToIndiceProximoChar()
			{
				LexemeStart = indiceProximoChar;
			}
		}

		private struct Position
		{
			internal int Row, Column, Indice;
		}

		private struct Positions
		{
			private readonly List<Position> positions;
			private int index;

			internal Positions(int tamanoInicial = 32)
			{
				positions = new List<Position>(tamanoInicial);
				index = -1;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void ResetForNextToken()
			{
				index = -1;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void SavePosition(int row, int column, int currentIndex)
			{
				index++;
				if (positions.Count > index)
				{
					positions[index] = new Position { Row = row, Column = column, Indice = currentIndex };
				}
				else
				{
					positions.Add(new Position { Row = row, Column = column, Indice = currentIndex });
				}
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			internal void RemoveLastPosition()
			{
				index--;
			}

			internal int Row
			{
				get
				{
					return this.positions[index].Row;
				}
			}

			internal int Column
			{
				get
				{
					return this.positions[index].Column;
				}
			}

			internal int IndiceActual
			{
				get
				{
					return this.positions[index].Indice;
				}
			}
		}

	}
}
