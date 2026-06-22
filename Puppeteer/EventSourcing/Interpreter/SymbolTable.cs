using Puppeteer.Tell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace Puppeteer.EventSourcing.Interpreter
{
	internal class SymbolTable
	{
		private readonly IDictionary<string, VariableSymbol> variablesByName = new Dictionary<string, VariableSymbol>(StringComparer.OrdinalIgnoreCase);

		// Map name -> signature (canonical source) of upgrades applied to the actor.
		// Sibling of the symbols dict, NOT merged: symbols are domain state,
		// appliedUpgrades is metadata of the actor's lifecycle.
		private readonly Dictionary<string, string> appliedUpgrades = new Dictionary<string, string>(StringComparer.Ordinal);

		// Two-table dedup for tells, optimised for the high-frequency case (tells run
		// far more often than upgrade('label'), so discrimination has to be fast and
		// alloc-free where possible).
		//
		//   - When the developer supplies `id 'X'` explicitly, the X string is kept
		//     verbatim — no fabricated label, no extra allocation, hash via Ordinal.
		//   - When the developer omits the id, Plan 5 computes a deterministic long
		//     (FNV-1a 64-bit over target | targetIdValue | commandName | argValues)
		//     and indexes by that long. No string is allocated on the lookup path.
		//
		// upgrade('label') is left untouched — labels are few per actor and
		// signature validation is part of its contract, so the existing
		// HashSet<string>-shaped appliedUpgrades dict still fits there.
		private readonly HashSet<long> appliedImplicitTellHashes = new HashSet<long>();
		private readonly HashSet<string> appliedExplicitTellIds = new HashSet<string>(StringComparer.Ordinal);

		// Map from envelope.Id (the string the transport delivers in tell + ack) to
		// the journal entry id where the originating tell sentence was persisted.
		// Plan 6 of the Tell primitive roadmap uses it to find the tell entry when
		// the matching ack arrives, so the framework can MarkAsSkip the tell+ack
		// pair (when both entries are single-tell / single-ack respectively).
		// Survives across actor restarts via journal replay reconstructing the map.
		private readonly Dictionary<string, long> tellEnvelopeIdToEntryId = new Dictionary<string, long>(StringComparer.Ordinal);

		// Subset of tell entry ids that the framework has determined are
		// "single-tell entries" — the script that produced them contains exactly
		// one TellStatement (and nothing else). Only these are eligible for
		// pair-elision at ack time, because MarkEventsAsElided operates at entry
		// granularity. Multi-statement entries (e.g. an upgrade plus a tell on
		// the same PerformCmd) are NOT eligible — eliding them would discard the
		// non-tell siblings as collateral damage.
		private readonly HashSet<long> singleTellEntryIds = new HashSet<long>();

		// Plan 8 of the Tell primitive roadmap: per-saga-instance cursor that
		// tracks the saga state machine (NotStarted -> Open -> Closed) and the
		// list of journal entry ids that form the saga's trajectory. The cursor
		// is keyed by the canonical "sagaActor|sagaIdValue" string. On `close`
		// the framework emits MarkEventsAsElided over the whole TrajectoryEntryIds
		// list — claim 12 of Paper 3 generalised to cross-actor processes.
		// Survives across actor restarts via journal replay reconstructing the
		// cursor through TellSagaStatement.Execute(RecoveringState=true).
		private readonly Dictionary<string, SagaCursor> sagaCursors = new Dictionary<string, SagaCursor>(StringComparer.Ordinal);

		// Set of envelope ids that have already received an ack from the receiver.
		// Plan 6 of the Tell primitive roadmap uses it to reject duplicate acks
		// (the same envelope.Id arriving a second time is a transport bug or a
		// retry that should not produce a second journal entry). Strings are kept
		// verbatim — the envelope.Id either is the developer's explicit `id 'X'`
		// or is the 16-char hex of the implicit hash; both are reused as-is.
		// During replay TellAckStatement.Execute repopulates this set.
		private readonly HashSet<string> ackedTellEnvelopeIds = new HashSet<string>(StringComparer.Ordinal);

		// Buffer of TellEnvelope produced during program.Execute(). The handler drains
		// it post-journal-write, outside the actor's write lock, so transport latency
		// does not block subsequent commands. Plan 5 introduces the buffer; Plan 6
		// will drain ack envelopes through the transport's RegisterAckHandler.
		private readonly ConcurrentQueue<TellEnvelope> pendingTells = new ConcurrentQueue<TellEnvelope>();

		internal bool InEvalMode = false;

		private static readonly bool StrictQueryValidation = GetStrictValidationSetting();
		private bool _readOnlyMode = false;

		private static bool GetStrictValidationSetting()
		{
#if DEBUG
			return true;
#else
			var envVar = Environment.GetEnvironmentVariable("PUPPETEER_STRICT_QUERY_VALIDATION");
			if (string.IsNullOrEmpty(envVar))
				return false;
			return envVar.ToLower() == "true";
#endif
		}

		internal void SetReadOnlyMode(bool readOnly)
		{
			_readOnlyMode = readOnly;
		}

		internal SymbolTable()
		{
		}

		internal bool RecoveringState { get; set; } = false;

		// Transient check that TellStatement.Execute bakes into the TellEnvelope.Check
		// of the tells emitted during the body of a Causation.Continue(check:, ...).
		// ActorHandler.CausationTellCheck sets/clears it around that PerformCmd.
		// It is not evaluated here; it travels to the receiver to run as CheckThenCommand.
		internal string CurrentCausationCheck { get; set; }

		internal IEnumerable<string> Symbols
		{
			get
			{
				return variablesByName.Keys;
			}
		}

		// Enumerator of the actor's real globals — used by IActorIntrospection
		// to emit 'show symbols' / 'show symbol <name>'. Does NOT include cacheVariables
		// (transient to the in-progress execution) nor block locals (which never
		// reach the table by construction of the parser/interpreter).
		internal IEnumerable<VariableSymbol> EnumerateGlobalSymbols()
		{
			return variablesByName.Values;
		}

		internal object Value(string variableName)
		{
			return ValueGetter(variableName)();
		}

		internal void SetVariable(string instanceName, object dato, Type type)
		{
			VariableSymbol storedSymbol = null;
			bool existeVariable = variablesByName.TryGetValue(instanceName, out storedSymbol);

			if (StrictQueryValidation && _readOnlyMode && existeVariable)
			{
				throw new LanguageException($"[STRICT-MODE] Query attempted to modify the global variable '{instanceName}'. Queries must be read-only.");
			}

			if (existeVariable)
			{
				if (dato != null)
				{
					Type previousType = storedSymbol.value?.GetType();
					Type newType = dato.GetType();
					bool sonDelMismoTipo = previousType == null || newType == previousType ||
											newType.IsSubclassOf(previousType.BaseType);
					if (!sonDelMismoTipo)
					{
						throw new LanguageException($"Instance '{instanceName}' can only be assigned values of type '{storedSymbol.value.GetType().Name}', but a value of type '{dato.GetType().Name}' is being assigned.");
					}
					storedSymbol.value = dato;
					if (type != null)
					{
						storedSymbol.type = type;
					}
					else
					{
						storedSymbol.type = newType;
					}
				}
				else
				{
				}
			}
			else
			{
				VariableSymbol s = new VariableSymbol(instanceName, dato, type);
				variablesByName[instanceName] = s;
			}
		}

		internal void SetVariable(string instanceName, VariableSymbol symbol)
		{
			ArgumentNullException.ThrowIfNull(symbol);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(instanceName);

			variablesByName[instanceName] = symbol;
		}

		private readonly Dictionary<string, VariableSymbol> cacheVariables = new Dictionary<string, VariableSymbol>();
		internal void SetVariableCached(string instanceName, object dato, Type type)
		{
			bool existeVariableGlobal = variablesByName.ContainsKey(instanceName);

			if (StrictQueryValidation && _readOnlyMode && existeVariableGlobal)
			{
				throw new LanguageException($"[STRICT-MODE] Query attempted to modify the global variable '{instanceName}'. Queries must be read-only.");
			}

			VariableSymbol storedSymbol;
			if (cacheVariables.TryGetValue(instanceName, out storedSymbol))
			{
				storedSymbol.value = dato;
				if (type != null)
				{
					storedSymbol.type = type;
				}
				else if (dato != null)
				{
					storedSymbol.type = dato.GetType();
				}
			}
			else
			{
				SetVariable(instanceName, dato, type);
				storedSymbol = variablesByName[instanceName];
				cacheVariables.Add(instanceName, storedSymbol);
			}
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		internal static VariableSymbol IsolatedStorage(string name)
		{
			return IsolatedStorage(name, null, typeof(object));
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		internal static VariableSymbol IsolatedStorage(string name, object value, Type type)
		{
			VariableSymbol s = new VariableSymbol(name, value, type);
			return s;
		}

		internal bool HasVariable(string instanceName)
		{
			bool existe = variablesByName.ContainsKey(instanceName);
			return existe;
		}

		private VariableSymbol LookupVariable(string instanceName)
		{
			VariableSymbol result = null;
			if (variablesByName.TryGetValue(instanceName, out result))
			{
				return result;
			}
			return null;
		}

		internal Func<object> ValueGetter(string instanceName)
		{
			VariableSymbol variable = LookupVariable(instanceName);
			if (variable == null)
				return null;
			else
				return () => { return variable.value; };
		}

		internal VariableSymbol Entry(string instanceName)
		{
			VariableSymbol variable = LookupVariable(instanceName);
			if (variable == null)
				return null;
			else
				return variable;
		}

		internal bool IsUpgradeApplied(string name)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			return appliedUpgrades.ContainsKey(name);
		}

		internal void ValidateUpgradeSignature(string name, string signature)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			ArgumentNullException.ThrowIfNull(signature);
			if (appliedUpgrades.TryGetValue(name, out var existing) && existing != signature)
			{
				throw new LanguageException(
					$"Upgrade '{name}' differs from the one previously applied to this actor. " +
					$"To change the logic of an already-applied upgrade, give it a new name (e.g. '{name}_v2').\n\n" +
					$"Applied:\n{existing}\n\nAttempting to apply:\n{signature}");
			}
		}

		internal void MarkUpgradeApplied(string name, string signature)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			ArgumentNullException.ThrowIfNull(signature);
			appliedUpgrades[name] = signature;
		}

		// Reference back to the ActorHandler that owns this SymbolTable. The
		// ActorHandler ctor sets it after constructing the SymbolTable. Statements
		// (notably TellStatement.Execute) read it to reach the configured Transport.
		// Stays nullable defensively — code that depends on it must check.
		internal ActorHandler ActorHandler { get; set; }

		// Implicit tells: developer omitted `id 'X'`. Plan 5 indexes by a deterministic
		// long produced via FNV-1a 64-bit over the tell's components. Lookup is a
		// HashSet<long> contains — no string allocation on the hot path, including
		// during journal replay.
		internal bool IsImplicitTellApplied(long hash)
		{
			return appliedImplicitTellHashes.Contains(hash);
		}

		internal void MarkImplicitTellApplied(long hash)
		{
			appliedImplicitTellHashes.Add(hash);
		}

		// Explicit tells: developer wrote `id 'X'`. The string `X` is captured by the
		// parser; we keep the same reference verbatim — no fabricated label, no
		// re-allocation. StringComparer.Ordinal hashes/compares without culture work.
		internal bool IsExplicitTellApplied(string id)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(id);
			return appliedExplicitTellIds.Contains(id);
		}

		internal void MarkExplicitTellApplied(string id)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(id);
			appliedExplicitTellIds.Add(id);
		}

		// Records the (envelope.Id -> tell entry id) association at the moment
		// the tell statement runs, so HandleAckEnvelope can look up the
		// originating entry when the ack arrives. Idempotent: rewriting the
		// mapping with the same envelope id is a no-op when the entryId matches.
		internal void RegisterTellEnvelopeEntry(string envelopeId, long entryId)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
			tellEnvelopeIdToEntryId[envelopeId] = entryId;
		}

		internal bool TryLookupTellEntryId(string envelopeId, out long entryId)
		{
			return tellEnvelopeIdToEntryId.TryGetValue(envelopeId, out entryId);
		}

		// Marks an entry id as a single-tell entry — its script contains exactly
		// one TellStatement and nothing else. The framework's ack-side elision
		// only fires when this is true: eliding a multi-statement entry would
		// discard non-tell siblings on the same line.
		internal void MarkSingleTellEntry(long entryId)
		{
			singleTellEntryIds.Add(entryId);
		}

		internal bool IsSingleTellEntry(long entryId)
		{
			return singleTellEntryIds.Contains(entryId);
		}

		// True iff `envelopeId` was sent as an outbound tell at any point (live or
		// replayed from journal). Tries the cheap explicit lookup first; falls back
		// to parsing the 16-char lowercase hex into the implicit hash. Returns
		// false on every other shape — that is exactly the orphan-ack signal.
		internal bool IsTellEnvelopeIdKnown(string envelopeId)
		{
			if (string.IsNullOrEmpty(envelopeId)) return false;
			if (appliedExplicitTellIds.Contains(envelopeId)) return true;
			if (envelopeId.Length == 16 && long.TryParse(envelopeId, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long hash))
			{
				return appliedImplicitTellHashes.Contains(hash);
			}
			return false;
		}

		internal bool IsTellEnvelopeIdAcked(string envelopeId)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
			return ackedTellEnvelopeIds.Contains(envelopeId);
		}

		internal void MarkTellEnvelopeIdAcked(string envelopeId)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
			ackedTellEnvelopeIds.Add(envelopeId);
		}

		// Plan 8 helpers — saga cursor lifecycle.

		internal SagaCursor GetOrCreateSagaCursor(string sagaActor, string sagaIdValue)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(sagaActor);
			ArgumentException.ThrowIfNullOrWhiteSpace(sagaIdValue);
			string key = sagaActor + "|" + sagaIdValue;
			if (!sagaCursors.TryGetValue(key, out SagaCursor cursor))
			{
				cursor = new SagaCursor(sagaActor, sagaIdValue);
				sagaCursors[key] = cursor;
			}
			return cursor;
		}

		internal SagaCursor TryGetSagaCursor(string sagaActor, string sagaIdValue)
		{
			if (string.IsNullOrEmpty(sagaActor) || string.IsNullOrEmpty(sagaIdValue)) return null;
			sagaCursors.TryGetValue(sagaActor + "|" + sagaIdValue, out SagaCursor cursor);
			return cursor;
		}

		internal void EnqueuePendingTell(TellEnvelope envelope)
		{
			pendingTells.Enqueue(envelope);
		}

		internal bool TryDequeuePendingTell(out TellEnvelope envelope)
		{
			return pendingTells.TryDequeue(out envelope);
		}

		internal int PendingTellCount => pendingTells.Count;
	}

	internal class VariableSymbol
	{
		internal string name;
		internal object value;
		internal Type type;

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		internal VariableSymbol(string name, object value, Type type)
		{
			this.name = name;
			this.value = value;
			this.type = type;
		}
	}

	// Plan 8 of the Tell primitive roadmap: state machine + trajectory tracking
	// for a single saga instance. Cursor lifecycle:
	//
	//   NotStarted --start--> Open --step/compensate--> Open --close--> Closed
	//
	// Live execution enforces the transitions strictly (illegal transitions
	// raise LanguageException). Replay rebuilds the cursor from journal entries
	// and is tolerant — already-violated invariants on the storage are journal
	// facts, not new errors to raise.
	//
	// TrajectoryEntryIds accumulates the journal entry id of every saga
	// statement (start, step, compensate, close) executed against this cursor.
	// On `close` the framework emits MarkEventsAsElided over the whole list —
	// the saga's trajectory becomes a skipable group at rehydration.
	internal enum SagaState
	{
		NotStarted,
		Open,
		Closed
	}

	internal class SagaCursor
	{
		internal string SagaActor { get; }
		internal string SagaIdValue { get; }
		internal SagaState State { get; private set; }
		internal List<long> TrajectoryEntryIds { get; }

		internal SagaCursor(string sagaActor, string sagaIdValue)
		{
			SagaActor = sagaActor;
			SagaIdValue = sagaIdValue;
			State = SagaState.NotStarted;
			TrajectoryEntryIds = new List<long>();
		}

		// Live transitions raise on illegal moves so a buggy script fails fast.
		// Replay calls the lenient *AtReplay variants below.

		internal void TransitionStart(long entryId)
		{
			if (State != SagaState.NotStarted)
			{
				throw new LanguageException($"Saga '{SagaActor}({SagaIdValue})' cannot be started: it is already in state '{State}'. Each sagaId can only be started once.");
			}
			State = SagaState.Open;
			RecordEntry(entryId);
		}

		internal void TransitionStep(long entryId)
		{
			if (State != SagaState.Open)
			{
				throw new LanguageException($"Saga '{SagaActor}({SagaIdValue})' cannot accept a step: it is in state '{State}', not Open.");
			}
			RecordEntry(entryId);
		}

		internal void TransitionCompensate(long entryId)
		{
			if (State != SagaState.Open)
			{
				throw new LanguageException($"Saga '{SagaActor}({SagaIdValue})' cannot be compensated: it is in state '{State}', not Open.");
			}
			RecordEntry(entryId);
		}

		internal void TransitionClose(long entryId)
		{
			if (State != SagaState.Open)
			{
				throw new LanguageException($"Saga '{SagaActor}({SagaIdValue})' cannot be closed: it is in state '{State}', not Open.");
			}
			RecordEntry(entryId);
			State = SagaState.Closed;
		}

		// Replay variants: rebuild state from the journal without raising on
		// previously-applied transitions (they are facts of the storage by now).
		internal void ReplayStart(long entryId)
		{
			State = SagaState.Open;
			RecordEntry(entryId);
		}

		internal void ReplayStep(long entryId) { RecordEntry(entryId); }
		internal void ReplayCompensate(long entryId) { RecordEntry(entryId); }
		internal void ReplayClose(long entryId)
		{
			RecordEntry(entryId);
			State = SagaState.Closed;
		}

		private void RecordEntry(long entryId)
		{
			if (entryId > 0) TrajectoryEntryIds.Add(entryId);
		}
	}

}
