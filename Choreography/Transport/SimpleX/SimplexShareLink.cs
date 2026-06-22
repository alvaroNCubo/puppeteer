using System;
using System.Collections.Generic;
using System.Linq;

namespace Choreography.Transport.SimpleX
{
    // Decoder de share-link de SimpleX Chat. Spec en
    // simplexmq/src/Simplex/Messaging/Agent/Protocol.hs (StrEncoding ConnectionRequestUri)
    // y simplexmq/src/Simplex/Messaging/ServiceScheme.hs.
    //
    // Format:
    //   {scheme}/{mode}#/?{queryString}
    //
    //   scheme = "simplex:" | "https://{host}[:{port}]"   (e.g. https://simplex.chat)
    //   mode   = "invitation" | "contact"
    //   queryString = "v=...&smp=...&e2e=...&data=..." (URL-encoded values)
    //
    //   smp = ;-separated list of queue URIs (format smp://HASH@host/sndId#/dhKey)
    //   v   = agent version range (ignored by Choreography)
    //   e2e = ratchet params (relevant only if Choreography implements Double Ratchet)
    //   data = optional client data (text)
    //
    // Choreography uses neither e2e nor v; this decoder extracts only smp queues. The host
    // can emit share-links if it wants interop with the official SimpleX Chat; Stages consume
    // queues directly.
    internal static class SimplexShareLink
    {
        public sealed class DecodedShareLink
        {
            public string Scheme { get; init; }       // "simplex:" o "https://server"
            public string Mode { get; init; }         // "invitation" o "contact"
            public SmpQueue[] Queues { get; init; }   // 1+ SMP queues
            public string AgentVersionRange { get; init; }  // raw "v" param, optional
            public string RatchetParams { get; init; }      // raw "e2e" param, optional
            public string ClientData { get; init; }         // raw "data" param, optional (URL-decoded)
        }

        public static DecodedShareLink Decode(string shareLink)
        {
            if (string.IsNullOrWhiteSpace(shareLink))
                throw new ArgumentNullException(nameof(shareLink));

            // 1. Scheme
            string scheme;
            int afterScheme;
            if (shareLink.StartsWith("simplex:", StringComparison.Ordinal))
            {
                scheme = "simplex:";
                afterScheme = "simplex:".Length;
            }
            else if (shareLink.StartsWith("https://", StringComparison.Ordinal))
            {
                // Capture full "https://host[:port]" up to next '/'
                int hostEnd = shareLink.IndexOf('/', "https://".Length);
                if (hostEnd < 0) throw new ArgumentException("Invalid share link: no path after https://host");
                scheme = shareLink.Substring(0, hostEnd);
                afterScheme = hostEnd;
            }
            else
            {
                throw new ArgumentException($"Invalid share link scheme. Expected 'simplex:' or 'https://...'");
            }

            // 2. Mode: '/' + ("invitation" | "contact")
            int hashIdx = shareLink.IndexOf('#', afterScheme);
            if (hashIdx < 0) throw new ArgumentException("Invalid share link: no '#' fragment");

            string pathPart = shareLink.Substring(afterScheme, hashIdx - afterScheme);
            // pathPart should be "/invitation" or "/contact" (optionally trailing '/')
            string mode = pathPart.TrimStart('/').TrimEnd('/');
            if (mode != "invitation" && mode != "contact")
                throw new ArgumentException($"Invalid share link mode '{mode}'. Expected 'invitation' or 'contact'");

            // 3. Fragment + query: skip "#" then optional "/?", then key=value pairs
            string fragment = shareLink.Substring(hashIdx + 1);
            if (fragment.StartsWith("/?", StringComparison.Ordinal)) fragment = fragment.Substring(2);
            else if (fragment.StartsWith("?", StringComparison.Ordinal)) fragment = fragment.Substring(1);
            else if (fragment.StartsWith("/", StringComparison.Ordinal)) fragment = fragment.Substring(1);

            // 4. Parse query string
            var query = ParseQuery(fragment);

            if (!query.TryGetValue("smp", out string smpRaw) || string.IsNullOrEmpty(smpRaw))
                throw new ArgumentException("Share link missing required 'smp' query parameter");

            // 5. URL-decode and split by ';'
            string smpDecoded = Uri.UnescapeDataString(smpRaw);
            string[] queueParts = smpDecoded.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (queueParts.Length == 0)
                throw new ArgumentException("Share link 'smp' parameter has no queue URIs");

            SmpQueue[] queues = queueParts
                .Select(p => SmpQueue.FromInvitationUri(p.Trim()))
                .ToArray();

            return new DecodedShareLink
            {
                Scheme = scheme,
                Mode = mode,
                Queues = queues,
                AgentVersionRange = query.GetValueOrDefault("v"),
                RatchetParams = query.GetValueOrDefault("e2e"),
                ClientData = query.TryGetValue("data", out string dataRaw)
                    ? Uri.UnescapeDataString(dataRaw)
                    : null
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return result;

            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    result[pair] = string.Empty;
                }
                else
                {
                    string key = pair.Substring(0, eq);
                    string value = pair.Substring(eq + 1);
                    result[key] = value;
                }
            }
            return result;
        }
    }
}
