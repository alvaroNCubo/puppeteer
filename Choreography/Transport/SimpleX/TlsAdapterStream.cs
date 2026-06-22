using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Choreography.Transport.SimpleX
{
    // Adapter Stream that wraps BC.Tls TlsClientProtocol in non-blocking mode to
    // resolve the concurrent read+write deadlock (Bug A) of the blocking TlsStream
    // API.
    //
    // Architecture:
    //   - 1 background reader task: reads encrypted bytes from the TCP socket -> OfferInput.
    //     Extracts plaintext from the internal TLS queue via ReadInput and feeds it into a
    //     System.IO.Pipelines.Pipe.
    //   - WriteAsync (public): takes _tlsLock, calls WriteApplicationData (encrypts),
    //     drains output to the socket under _socketWriteLock. Releases _tlsLock.
    //   - ReadAsync (public): reads from the Pipe (does not touch TlsProtocol). The reader task
    //     fills it in the background.
    //   - Output draining happens both after WriteApplicationData (in the writer)
    //     and after OfferInput (in the reader, in case it produced alerts/handshake).
    //
    // A single _tlsLock serializes TlsProtocol mutations; it allows concurrent app
    // read+write because the app-read only touches the Pipe (not TLS).
    internal sealed class TlsAdapterStream : Stream
    {
        private const int IoBufferSize = 16384;

        private readonly TlsClientProtocol _tlsProtocol;
        private readonly NetworkStream _socketStream;
        private readonly SemaphoreSlim _tlsLock = new(1, 1);
        private readonly SemaphoreSlim _socketWriteLock = new(1, 1);
        private readonly Pipe _plaintextPipe = new();
        private readonly CancellationTokenSource _readerCts = new();
        private Task _readerTask;
        private volatile bool _disposed;

        public TlsAdapterStream(NetworkStream socketStream)
        {
            _socketStream = socketStream ?? throw new ArgumentNullException(nameof(socketStream));
            _tlsProtocol = new TlsClientProtocol(); // non-blocking mode
        }

        // Drive the TLS handshake until the NotifyHandshakeComplete flag is true
        // (captured in ISmpTlsClient.HandshakeComplete). Then start the reader task.
        public async Task PerformHandshakeAsync(ISmpTlsClient tlsClient, CancellationToken ct)
        {
            if (tlsClient == null) throw new ArgumentNullException(nameof(tlsClient));

            _tlsProtocol.Connect((TlsClient)tlsClient);

            byte[] inBuf = new byte[IoBufferSize];
            while (!tlsClient.HandshakeComplete)
            {
                // 1. Drain any output the protocol generated (e.g. client hello, key exchange).
                await DrainTlsOutputAsync(ct);

                if (tlsClient.HandshakeComplete) break;

                // 2. Read encrypted bytes from socket.
                int n = await _socketStream.ReadAsync(inBuf, 0, inBuf.Length, ct);
                if (n == 0) throw new IOException("Socket closed during TLS handshake");

                _tlsProtocol.OfferInput(inBuf, 0, n);
            }

            // Final drain in case handshake ended with output pending
            await DrainTlsOutputAsync(ct);

            // Steady-state: reader task continuously feeds plaintext pipe.
            _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token));
        }

        private async Task ReaderLoopAsync(CancellationToken ct)
        {
            byte[] inBuf = new byte[IoBufferSize];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = await _socketStream.ReadAsync(inBuf, 0, inBuf.Length, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }

                    if (n == 0) break;

                    await _tlsLock.WaitAsync(ct);
                    try
                    {
                        _tlsProtocol.OfferInput(inBuf, 0, n);

                        // Extract all plaintext available
                        int avail;
                        while ((avail = _tlsProtocol.GetAvailableInputBytes()) > 0)
                        {
                            var mem = _plaintextPipe.Writer.GetMemory(avail);
                            int len = Math.Min(mem.Length, avail);
                            // ReadInput needs a byte[] backing
                            byte[] tmp = new byte[len];
                            int read = _tlsProtocol.ReadInput(tmp, 0, len);
                            tmp.AsSpan(0, read).CopyTo(mem.Span);
                            _plaintextPipe.Writer.Advance(read);
                        }

                        // OfferInput may have produced output (e.g. alerts, NewSessionTicket).
                        await DrainTlsOutputAsync(ct);
                    }
                    finally
                    {
                        _tlsLock.Release();
                    }

                    await _plaintextPipe.Writer.FlushAsync(ct);
                }
            }
            finally
            {
                await _plaintextPipe.Writer.CompleteAsync();
            }
        }

        // Called with _tlsLock held or during the handshake (single-threaded). Drains all
        // pending TlsProtocol output to the socket. Takes _socketWriteLock in case some
        // future external task writes to the socket directly (not the case today, defensive).
        private async Task DrainTlsOutputAsync(CancellationToken ct)
        {
            int avail;
            while ((avail = _tlsProtocol.GetAvailableOutputBytes()) > 0)
            {
                byte[] tmp = new byte[avail];
                int read = _tlsProtocol.ReadOutput(tmp, 0, avail);

                await _socketWriteLock.WaitAsync(ct);
                try
                {
                    await _socketStream.WriteAsync(tmp, 0, read, ct);
                    await _socketStream.FlushAsync(ct);
                }
                finally
                {
                    _socketWriteLock.Release();
                }
            }
        }

        // --- Stream overrides ---

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            // Block until at least 1 byte arrives (or pipe closes).
            var result = await _plaintextPipe.Reader.ReadAtLeastAsync(1, cancellationToken);
            var seq = result.Buffer;
            if (seq.IsEmpty)
            {
                if (result.IsCompleted) return 0;
                _plaintextPipe.Reader.AdvanceTo(seq.End);
                return 0;
            }
            int copy = Math.Min((int)seq.Length, buffer.Length);
            BuffersExtensions.CopyTo(seq.Slice(0, copy), buffer.Span);
            _plaintextPipe.Reader.AdvanceTo(seq.GetPosition(copy));
            return copy;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] arr = buffer.ToArray();
            await _tlsLock.WaitAsync(cancellationToken);
            try
            {
                _tlsProtocol.WriteApplicationData(arr, 0, arr.Length);
                await DrainTlsOutputAsync(cancellationToken);
            }
            finally
            {
                _tlsLock.Release();
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { /* writes already flush in WriteAsync */ }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try { _readerCts.Cancel(); } catch { }
                try { _tlsProtocol.Close(); } catch { }
                try { _readerCts.Dispose(); } catch { }
                _tlsLock.Dispose();
                _socketWriteLock.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Marker interface so TlsAdapterStream can query the handshake-complete flag
    // captured by the NotifyHandshakeComplete callback.
    internal interface ISmpTlsClient
    {
        bool HandshakeComplete { get; }
    }
}
