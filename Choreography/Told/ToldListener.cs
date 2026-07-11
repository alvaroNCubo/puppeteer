using System;
using System.Collections.Generic;
using Choreography.Observability;
using Choreography.Theater;
using Choreography.Transport.Brokered;
using Puppeteer;

namespace Choreography.Told
{
    // Receiver-side uptake of a cross-actor `tell`, expressed declaratively — the
    // dual of the sender's `tell <Message> to <Addressee>`. The sender asserts a
    // fact in its own vocabulary; the hearer decides which command that fact runs.
    //
    // This raises the receiver from hand-wired plumbing (a magic topic string plus
    // an imperative `if (MessageName == ...) PerformCommand(...)` switch) to a
    // declaration in the hearer's own language:
    //
    //   using var listener = performance
    //       .ListenAs("Facturador", bindings, broker)     // role -> topic, from the SAME table the sender routes by
    //       .Told("NuevaMoneda").With<string>("code").With<string>("symbol")
    //           .Command("Add(@code, @symbol);")           // the command the hearer already owns
    //       .Start();
    //
    // Layering: this is the tell-AWARE receiver. It builds on the medium-agnostic
    // input seam (BrokerTellConsumer over IInputSource) and adds what a directed
    // speech act requires — uptake as ACK AFTER COMMIT, idempotency, and identity.
    // A `tell` without uptake is infelicitous; the hearer running (and only then
    // acking) IS the uptake that closes the act.
    //
    // The hearer may respond with ANY of its operations, because it is the single-
    // writer author of its own journal and the told is just an input source. Beyond
    // .Command(...) (run a command), a mapping may terminate with .Enact(...): lower
    // an authoring notation into a journaled Action via the receiver's transpiler —
    // "A's tell IS B's PerformEnact", the receiver-side dual of an endpoint's Enact.
    //
    // Scope note (v1): the surface lives on the Performance host and reuses the
    // broker consumer and the Parameters signature machinery. Hoisting `Told` into
    // the core (ActorHandler/Reactions, exposed identically on every host) is a
    // planned follow-up; the vocabulary here is the one meant to survive that move.
    public sealed class ToldListener : IDisposable
    {
        private readonly PerformanceV2 receiver;
        private readonly string role;
        private readonly BrokerTellConsumer consumer;
        private readonly IPuppeteerLogger logger;

        // messageName (the sender's vocabulary, case-insensitive) -> the command the
        // hearer runs and the typed signature its carried values rebind into.
        private readonly Dictionary<string, ToldMapping> mappings =
            new Dictionary<string, ToldMapping>(StringComparer.OrdinalIgnoreCase);

        // In-process idempotency: an envelope id already applied is acked again (the
        // prior ack may have been lost) but never re-run. Cross-restart idempotency
        // is the receiver's own `Check` — the sender bakes it into the envelope via
        // .Causation.Continue(check:, "tell ..."), and it is honored here.
        private readonly HashSet<string> appliedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object appliedLock = new object();

        private bool started;
        private bool disposed;

        internal ToldListener(PerformanceV2 receiver, IMessageBroker broker, string topic, string role, IPuppeteerLogger logger)
        {
            this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            if (broker == null) throw new ArgumentNullException(nameof(broker));
            ArgumentException.ThrowIfNullOrWhiteSpace(topic);
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            this.role = role;
            this.logger = logger;
            this.consumer = new BrokerTellConsumer(broker, topic, logger);
        }

        // Begin a mapping for a message the hearer expects to be told. Terminate it
        // with .With<T>(...).Command(...) to run a command, or .With<T>(...).Enact(...)
        // to journal an Action lowered from an authoring notation. Must be called
        // before Start().
        public ToldBinding Told(string messageName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageName);
            if (started) throw new InvalidOperationException("Cannot register a Told mapping after Start().");
            if (disposed) throw new ObjectDisposedException(nameof(ToldListener));
            return new ToldBinding(this, messageName);
        }

        internal void Register(string messageName, string declaration, string text, bool isEnact)
        {
            if (started) throw new InvalidOperationException("Cannot register a Told mapping after Start().");
            if (mappings.ContainsKey(messageName))
                throw new InvalidOperationException($"A Told mapping for message '{messageName}' already exists on this listener.");
            mappings[messageName] = new ToldMapping(declaration, text, isEnact);
        }

        // Begin consuming. One-shot: registrations are frozen and the consumer is
        // wired. Returns this so it composes at the end of the fluent chain.
        public ToldListener Start()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ToldListener));
            if (started) throw new InvalidOperationException("ToldListener already started.");
            if (mappings.Count == 0)
                throw new InvalidOperationException("Register at least one Told(...).Command(...) or Told(...).Enact(...) before Start().");
            started = true;
            consumer.OnReceive(Apply);
            return this;
        }

        // BrokerTellConsumer contract: return true once the hearer's command has
        // committed (the signal to ack the origin); false leaves the record unacked.
        private bool Apply(ReceivedTell rt)
        {
            if (string.IsNullOrEmpty(rt.MessageName) || !mappings.TryGetValue(rt.MessageName, out ToldMapping mapping))
            {
                // A message this listener was not told to expect. Do not ack — the
                // origin stays pending and the gap is visible rather than silently
                // absorbed by a hearer that has no command for it.
                logger?.Debug($"[ToldListener:{role}] no Told mapping for message '{rt.MessageName}'; not acking '{rt.Id}'.");
                return false;
            }

            lock (appliedLock)
            {
                if (rt.Id != null && appliedIds.Contains(rt.Id)) return true; // already applied; re-ack only
            }

            Parameters values = mapping.BuildValues(rt.Arguments);

            // Open the uptake span, re-parenting onto the sender's trace when the tell
            // carried a W3C trace context. The span is Activity.Current while the hearer
            // runs, so an ONWARD tell the hearer emits within this scope inherits the
            // same trace and the multi-hop chain stays one distributed trace. No context
            // on the wire -> a normal span (no re-parent), preserving prior behavior.
            IFlowSpan span = ToldTracer.Instance.StartUptakeSpan(rt.MessageName, role, rt.TraceParent);
            try
            {
                // The sender's optional check is baked into the envelope so the HEARER
                // runs it as a CheckThenCommand against its own state — idempotent
                // fan-out without the origin knowing the hearer's state.
                //
                // An Enact mapping responds with the hearer's PerformEnact instead of
                // PerformCommand: the notation is lowered by the receiver's transpiler
                // and only the built parametric Action is journaled. That transpile is
                // AUTHOR-SIDE — it runs here, once, on the receiver Performance, exactly
                // as an HTTP-endpoint Enact does; replay re-issues the journaled Action
                // and never re-consumes the told nor re-runs the transpiler. If Told is
                // ever hoisted into a replayed Reaction body, this transform must STAY
                // author-side (the notation is an input, not a journaled fact).
                if (mapping.IsEnact)
                {
                    if (!string.IsNullOrEmpty(rt.Check))
                        receiver.PerformCheckThenEnact(rt.Check, mapping.Notation, values);
                    else
                        receiver.PerformEnact(mapping.Notation, values);
                }
                else if (!string.IsNullOrEmpty(rt.Check))
                    receiver.Using(rt.Check, mapping.Command).WithParameters(values).PerformCheckThenCommand();
                else
                    receiver.Using(mapping.Command).WithParameters(values).PerformCommand();
                span.SetOutcome(FlowOutcome.Success);
            }
            catch (Exception ex)
            {
                span.SetOutcome(FlowOutcome.Failure);
                logger?.Error($"[ToldListener:{role}] applying '{rt.MessageName}' (id '{rt.Id}') failed; not acking.", ex);
                return false;
            }
            finally
            {
                span.Dispose();
            }

            lock (appliedLock)
            {
                if (rt.Id != null) appliedIds.Add(rt.Id);
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            consumer.Dispose();
        }
    }

    // One registered mapping: the typed parameter signature (canonical declaration
    // text) the carried values rebind into, and the hearer's response — either a
    // command it runs (Command) or an authoring notation it enacts (Notation).
    // IsEnact selects which, keying the two response paths off one struct.
    internal readonly struct ToldMapping
    {
        private readonly string declaration; // "" when the message carries no payload
        private readonly string text;         // the command template OR the enact notation

        internal bool IsEnact { get; }
        internal string Command => text;      // the DSL the hearer runs (Command mapping)
        internal string Notation => text;     // the authoring notation the hearer enacts (Enact mapping)

        internal ToldMapping(string declaration, string text, bool isEnact)
        {
            this.declaration = declaration ?? string.Empty;
            this.text = text;
            IsEnact = isEnact;
        }

        // Rebuild the typed, named Parameters from the ordered VALUES that crossed
        // the wire. Only the values travel (the command shape is the hearer's), so
        // the hearer supplies the signature (the .With<T> declaration) and the values
        // are loaded positionally — the same signature+LoadArguments round-trip the
        // journal uses for an Action's arguments.
        internal Parameters BuildValues(string arguments)
        {
            if (string.IsNullOrEmpty(declaration)) return new Parameters();
            Parameters values = new Parameters(declaration);
            if (!string.IsNullOrEmpty(arguments)) values.LoadArguments(arguments);
            return values;
        }
    }

    // Fluent builder for one Told mapping: declare the carried payload's typed
    // signature, then the command the hearer runs.
    public sealed class ToldBinding
    {
        private readonly ToldListener owner;
        private readonly string messageName;
        private readonly List<string> declarationParts = new List<string>();

        internal ToldBinding(ToldListener owner, string messageName)
        {
            this.owner = owner;
            this.messageName = messageName;
        }

        // Declare the next positional value the message carries. Order matters: it
        // must match the order of the sender's `tell ... with v1, v2, ...`.
        public ToldBinding With<T>(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            declarationParts.Add("In," + name + ":" + TokenFor(typeof(T)));
            return this;
        }

        // The command the hearer already owns, referencing the declared values as
        // `@name`. Registers the mapping and returns the listener for chaining.
        public ToldListener Command(string commandTemplate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(commandTemplate);
            owner.Register(messageName, string.Join(",", declarationParts), commandTemplate, isEnact: false);
            return owner;
        }

        // Sibling of Command: respond by ENACTING an authoring notation rather than
        // running a fixed command. The notation is lowered by the receiver's
        // transpiler (Identity by default) into a parametric command body that may
        // reference the declared values as `@name`; only the built Action is
        // journaled, so replay reconstructs WITHOUT re-consuming the told or
        // re-running the transpiler. This is the receiver-side dual of "A's tell IS
        // B's PerformEnact" — the same author-side transform an HTTP endpoint runs.
        public ToldListener Enact(string notation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notation);
            owner.Register(messageName, string.Join(",", declarationParts), notation, isEnact: true);
            return owner;
        }

        // Canonical DSL type token, symmetric with the framework's parameter-type
        // writer so the signature round-trips through the same parser the journal uses.
        private static string TokenFor(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "Decimal";
            if (type == typeof(DateTime)) return "DateTime";
            throw new ArgumentException(
                $"Told .With<{type.Name}> is not a supported payload type. Supported: string, int, bool, double, decimal, DateTime.");
        }
    }
}
