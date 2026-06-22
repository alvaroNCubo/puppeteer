using System;

namespace Choreography.Theater
{
    // Authoring front-end seam (the input-side mirror of the output Formatter):
    // translates a domain authoring notation into a Puppeteer DSL command body.
    // It runs only at author-time and is NEVER invoked during replay — only the
    // transpiled body (the Action) is journaled, so the substrate reconstructs
    // without it. The journal is presentation-blind: it cannot tell which
    // front-end produced a given Action. Wired per-Performance, defaulting to
    // Identity so every Performance always carries one.
    public interface INotationTranspiler
    {
        string Transpile(string notation);
    }

    // Default front-end: the author already wrote Puppeteer DSL, so no
    // translation is needed. It makes the transpile concept total. Its presence,
    // pinned to identity, is precisely what every direct-DSL author has always
    // used implicitly — the operation simply makes it visible and variable.
    public sealed class IdentityTranspiler : INotationTranspiler
    {
        public static readonly IdentityTranspiler Instance = new IdentityTranspiler();

        private IdentityTranspiler() { }

        public string Transpile(string notation)
        {
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            return notation;
        }
    }
}
