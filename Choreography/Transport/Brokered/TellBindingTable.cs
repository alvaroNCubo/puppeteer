using System;
using System.Collections.Generic;

namespace Choreography.Transport.Brokered
{
	// Runtime binding table that resolves a tell's logical addressing to a physical
	// broker topic. It is the DNS-like indirection that keeps the transport out of
	// the DSL and the journal: the sender says only the logical name (the addressee
	// role, and the message it utters); deployment configuration binds that to a
	// topic. A `tell` therefore reads identically whatever transport the runtime
	// happens to use.
	//
	// Two granularities, most-specific-first:
	//   * (addressee, message) → topic — route a specific message to a specific
	//     hearer onto its own topic.
	//   * (addressee) → topic — route everything a hearer is told onto one topic.
	// A default topic is the last resort. Resolution throws when nothing matches, so
	// a misconfigured deployment fails loudly rather than dropping an utterance.
	public sealed class TellBindingTable
	{
		private readonly Dictionary<string, string> byAddressee = new Dictionary<string, string>(StringComparer.Ordinal);
		private readonly Dictionary<string, string> byAddresseeAndMessage = new Dictionary<string, string>(StringComparer.Ordinal);
		private readonly string defaultTopic;

		public TellBindingTable(string defaultTopic = null)
		{
			this.defaultTopic = string.IsNullOrWhiteSpace(defaultTopic) ? null : defaultTopic;
		}

		// Bind every utterance addressed to <addressee> onto <topic>.
		public TellBindingTable Bind(string addressee, string topic)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(addressee);
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			byAddressee[addressee] = topic;
			return this;
		}

		// Bind a specific <message> addressed to <addressee> onto <topic>. Takes
		// precedence over the addressee-only binding.
		public TellBindingTable Bind(string addressee, string message, string topic)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(addressee);
			ArgumentException.ThrowIfNullOrWhiteSpace(message);
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			byAddresseeAndMessage[Key(addressee, message)] = topic;
			return this;
		}

		// Resolve the destination topic for an utterance. Most-specific binding wins:
		// (addressee, message) → (addressee) → default. Throws when unresolved.
		public string Resolve(string addressee, string message)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(addressee);

			if (!string.IsNullOrWhiteSpace(message)
				&& byAddresseeAndMessage.TryGetValue(Key(addressee, message), out string byPair))
			{
				return byPair;
			}
			if (byAddressee.TryGetValue(addressee, out string byRole))
			{
				return byRole;
			}
			if (defaultTopic != null)
			{
				return defaultTopic;
			}
			throw new InvalidOperationException(
				$"No broker binding for addressee '{addressee}' (message '{message}'). "
				+ "Bind the addressee (and optionally the message) to a topic, or construct the binding table with a default topic.");
		}

		private static string Key(string addressee, string message)
		{
			return addressee + "|" + message;
		}
	}
}
