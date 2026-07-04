using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer;

namespace Choreography.Transport.Brokered
{
	// Cross-process, RAM-only IMessageBroker over the TCP loopback interface.
	// Like InProcessBroker it holds messages only in memory (no files, no
	// persistence — kill the host and everything is gone), but unlike it the
	// messages cross the process boundary, so two separately-launched services
	// (e.g. an Orders service and a Rewards service, each with its full startup)
	// can exchange tells on one dev box and be debugged with breakpoints in both.
	// TCP loopback is the
	// uniform choice across Windows/Linux/macOS.
	//
	// Topology: the first instance to bind 127.0.0.1:<port> hosts the in-RAM hub
	// (a topic fan-out with retained log); every instance — including the host —
	// also opens a client connection to that port for its own produce/subscribe.
	// So "run Rewards, then run Orders" needs no role flag: whoever starts first
	// hosts. If the host process is paused at a breakpoint, in-flight records wait
	// in the OS socket buffers until it resumes — nothing is lost.
	//
	// BCL-only (System.Net.Sockets + System.Text.Json), so it ships in core beside
	// InProcessBroker. It is a DEVELOPMENT substrate: no auth, no durability, no
	// backpressure tuning — for production use a real broker (e.g. Kafka).
	public sealed class LoopbackBroker : IMessageBroker, IDisposable
	{
		// Wire frame (length-prefixed UTF-8 JSON). Kind: "P" produce, "S" subscribe,
		// "R" record (hub -> subscriber).
		private sealed class Frame
		{
			public string Kind { get; set; }
			public string Topic { get; set; }
			public string Key { get; set; }
			public Dictionary<string, string> Headers { get; set; }
			public string Value { get; set; }
		}

		private readonly IPuppeteerLogger logger;
		private readonly Hub hub;                       // non-null only on the hosting instance
		private readonly TcpClient client;
		private readonly NetworkStream stream;
		private readonly object writeLock = new object();
		private readonly ConcurrentDictionary<string, List<Action<BrokerRecord>>> handlers =
			new ConcurrentDictionary<string, List<Action<BrokerRecord>>>(StringComparer.Ordinal);
		private readonly Thread receiveLoop;
		private volatile bool disposed;

		public int Port { get; }

		// port 0 => bind an ephemeral port and host (use Port to tell others where
		// to connect). A non-zero port already in use => connect as a pure client.
		public LoopbackBroker(int port = 0, IPuppeteerLogger logger = null)
		{
			this.logger = logger;

			if (Hub.TryStart(port, logger, out Hub started, out int boundPort))
			{
				hub = started;
				Port = boundPort;
			}
			else
			{
				hub = null;            // someone else already hosts this port
				Port = port;
			}

			client = new TcpClient();
			client.Connect(IPAddress.Loopback, Port);
			client.NoDelay = true;
			stream = client.GetStream();

			receiveLoop = new Thread(ReceiveLoop) { IsBackground = true, Name = $"loopback-broker-client:{Port}" };
			receiveLoop.Start();
		}

		public Task ProduceAsync(string topic, string key, IReadOnlyDictionary<string, string> headers, string value, CancellationToken cancellationToken = default)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			ArgumentNullException.ThrowIfNull(value);
			if (disposed) throw new ObjectDisposedException(nameof(LoopbackBroker));
			cancellationToken.ThrowIfCancellationRequested();

			Frame frame = new Frame
			{
				Kind = "P",
				Topic = topic,
				Key = key,
				Headers = headers == null ? null : new Dictionary<string, string>(headers, StringComparer.Ordinal),
				Value = value
			};
			SendFrame(frame);
			return Task.CompletedTask;
		}

		public IDisposable Subscribe(string topic, Action<BrokerRecord> onRecord)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			ArgumentNullException.ThrowIfNull(onRecord);
			if (disposed) throw new ObjectDisposedException(nameof(LoopbackBroker));

			List<Action<BrokerRecord>> list = handlers.GetOrAdd(topic, _ => new List<Action<BrokerRecord>>());
			lock (list) { list.Add(onRecord); }

			SendFrame(new Frame { Kind = "S", Topic = topic });
			return new Subscription(this, topic, onRecord);
		}

		private void SendFrame(Frame frame)
		{
			byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame);
			lock (writeLock)
			{
				WriteFrame(stream, payload);
			}
		}

		private void ReceiveLoop()
		{
			try
			{
				while (!disposed)
				{
					byte[] payload = ReadFrame(stream);
					if (payload == null) break;        // connection closed

					Frame frame = JsonSerializer.Deserialize<Frame>(payload);
					if (frame == null || frame.Kind != "R") continue;

					IReadOnlyDictionary<string, string> headers = frame.Headers ?? new Dictionary<string, string>(0);
					BrokerRecord record = new BrokerRecord(frame.Topic, frame.Key, headers, frame.Value ?? string.Empty);

					if (handlers.TryGetValue(frame.Topic, out List<Action<BrokerRecord>> list))
					{
						Action<BrokerRecord>[] snapshot;
						lock (list) { snapshot = list.ToArray(); }
						foreach (Action<BrokerRecord> h in snapshot)
						{
							try { h(record); }
							catch (Exception ex) { logger?.Error($"[LoopbackBroker] handler threw for topic '{frame.Topic}'", ex); }
						}
					}
				}
			}
			catch (Exception ex)
			{
				// On Dispose the socket close unblocks the read with an expected
				// exception — swallow it; only a live failure is worth logging.
				if (!disposed) logger?.Error("[LoopbackBroker] client receive loop ended", ex);
			}
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			try { client?.Close(); } catch { }
			try { receiveLoop?.Join(TimeSpan.FromSeconds(2)); } catch { }
			hub?.Dispose();
		}

		private sealed class Subscription : IDisposable
		{
			private readonly LoopbackBroker owner;
			private readonly string topic;
			private readonly Action<BrokerRecord> handler;
			private bool disposed;

			internal Subscription(LoopbackBroker owner, string topic, Action<BrokerRecord> handler)
			{
				this.owner = owner;
				this.topic = topic;
				this.handler = handler;
			}

			public void Dispose()
			{
				if (disposed) return;
				disposed = true;
				if (owner.handlers.TryGetValue(topic, out List<Action<BrokerRecord>> list))
				{
					lock (list) { list.Remove(handler); }
				}
			}
		}

		// ── Length-prefixed framing (4-byte big-endian length + UTF-8 JSON) ──

		private static void WriteFrame(Stream s, byte[] payload)
		{
			Span<byte> len = stackalloc byte[4];
			BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
			s.Write(len);
			s.Write(payload, 0, payload.Length);
			s.Flush();
		}

		private static byte[] ReadFrame(Stream s)
		{
			byte[] lenBuf = ReadExactly(s, 4);
			if (lenBuf == null) return null;
			int length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
			if (length < 0 || length > 64 * 1024 * 1024) throw new IOException($"Loopback frame length out of range: {length}");
			return ReadExactly(s, length) ?? throw new IOException("Loopback frame truncated");
		}

		private static byte[] ReadExactly(Stream s, int count)
		{
			byte[] buffer = new byte[count];
			int offset = 0;
			while (offset < count)
			{
				int read = s.Read(buffer, offset, count - offset);
				if (read == 0) return offset == 0 ? null : throw new IOException("Loopback stream closed mid-frame");
				offset += read;
			}
			return buffer;
		}

		// ── The in-RAM hub (server): topic fan-out with a retained log ──

		private sealed class Hub : IDisposable
		{
			private readonly TcpListener listener;
			private readonly IPuppeteerLogger logger;
			private readonly object gate = new object();
			private readonly Dictionary<string, List<Frame>> retained = new Dictionary<string, List<Frame>>(StringComparer.Ordinal);
			private readonly Dictionary<string, List<Conn>> subscribers = new Dictionary<string, List<Conn>>(StringComparer.Ordinal);
			private readonly List<Conn> connections = new List<Conn>();
			private Thread acceptLoop;
			private volatile bool disposed;

			private Hub(TcpListener listener, IPuppeteerLogger logger)
			{
				this.listener = listener;
				this.logger = logger;
			}

			internal static bool TryStart(int port, IPuppeteerLogger logger, out Hub hub, out int boundPort)
			{
				hub = null;
				boundPort = port;
				TcpListener listener = new TcpListener(IPAddress.Loopback, port);
				try
				{
					listener.Start();
				}
				catch (SocketException)
				{
					return false;     // port already hosted by another instance
				}
				boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
				hub = new Hub(listener, logger);
				hub.acceptLoop = new Thread(hub.AcceptLoop) { IsBackground = true, Name = $"loopback-broker-hub:{boundPort}" };
				hub.acceptLoop.Start();
				return true;
			}

			private void AcceptLoop()
			{
				try
				{
					while (!disposed)
					{
						TcpClient tcp = listener.AcceptTcpClient();
						tcp.NoDelay = true;
						Conn conn = new Conn(tcp);
						lock (gate) { connections.Add(conn); }
						Thread t = new Thread(() => ConnLoop(conn)) { IsBackground = true, Name = "loopback-broker-conn" };
						t.Start();
					}
				}
				catch (Exception ex)
				{
					if (!disposed) logger?.Error("[LoopbackBroker] hub accept loop ended", ex);
				}
			}

			private void ConnLoop(Conn conn)
			{
				try
				{
					while (!disposed)
					{
						byte[] payload = ReadFrame(conn.Stream);
						if (payload == null) break;
						Frame frame = JsonSerializer.Deserialize<Frame>(payload);
						if (frame == null) continue;

						if (frame.Kind == "S") OnSubscribe(conn, frame.Topic);
						else if (frame.Kind == "P") OnProduce(frame);
					}
				}
				catch (Exception ex)
				{
					if (!disposed) logger?.Error("[LoopbackBroker] hub connection loop ended", ex);
				}
			}

			private void OnSubscribe(Conn conn, string topic)
			{
				Frame[] backlog;
				lock (gate)
				{
					if (!subscribers.TryGetValue(topic, out List<Conn> list))
					{
						list = new List<Conn>();
						subscribers[topic] = list;
					}
					list.Add(conn);
					backlog = retained.TryGetValue(topic, out List<Frame> kept) ? kept.ToArray() : Array.Empty<Frame>();
				}
				// Replay the retained log so a late subscriber still sees prior records.
				foreach (Frame f in backlog) SendRecord(conn, f);
			}

			private void OnProduce(Frame frame)
			{
				Conn[] targets;
				lock (gate)
				{
					if (!retained.TryGetValue(frame.Topic, out List<Frame> kept))
					{
						kept = new List<Frame>();
						retained[frame.Topic] = kept;
					}
					kept.Add(frame);
					targets = subscribers.TryGetValue(frame.Topic, out List<Conn> list) ? list.ToArray() : Array.Empty<Conn>();
				}
				foreach (Conn c in targets) SendRecord(c, frame);
			}

			private void SendRecord(Conn conn, Frame produced)
			{
				Frame record = new Frame
				{
					Kind = "R",
					Topic = produced.Topic,
					Key = produced.Key,
					Headers = produced.Headers,
					Value = produced.Value
				};
				byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record);
				try
				{
					lock (conn.WriteLock) { WriteFrame(conn.Stream, payload); }
				}
				catch (Exception ex)
				{
					logger?.Error($"[LoopbackBroker] hub failed to deliver record on '{produced.Topic}'", ex);
				}
			}

			public void Dispose()
			{
				if (disposed) return;
				disposed = true;
				try { listener.Stop(); } catch { }
				lock (gate)
				{
					foreach (Conn c in connections) { try { c.Tcp.Close(); } catch { } }
					connections.Clear();
				}
			}

			private sealed class Conn
			{
				internal readonly TcpClient Tcp;
				internal readonly NetworkStream Stream;
				internal readonly object WriteLock = new object();

				internal Conn(TcpClient tcp)
				{
					Tcp = tcp;
					Stream = tcp.GetStream();
				}
			}
		}
	}
}
