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
    // Adapter Stream que envuelve BC.Tls TlsClientProtocol en non-blocking mode para
    // resolver el deadlock concurrent read+write (Bug A) que tiene la API blocking
    // de TlsStream.
    //
    // Arquitectura:
    //   - 1 background reader task: lee bytes encrypted del TCP socket -> OfferInput.
    //     Extrae plaintext de la internal queue de TLS via ReadInput y lo mete a un
    //     System.IO.Pipelines.Pipe.
    //   - WriteAsync (publico): toma _tlsLock, llama WriteApplicationData (encripta),
    //     drena output al socket bajo _socketWriteLock. Suelta _tlsLock.
    //   - ReadAsync (publico): lee del Pipe (no toca TlsProtocol). El reader task lo
    //     llena en background.
    //   - Drain de output ocurre tanto despues de WriteApplicationData (en el writer)
    //     como despues de OfferInput (en el reader, por si genero alerts/handshake).
    //
    // Single _tlsLock serializa mutaciones del TlsProtocol; permite read+write de la
    // app concurrentes porque la app-read solo toca el Pipe (no TLS).
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

        // Drive el handshake TLS hasta que el flag NotifyHandshakeComplete sea true
        // (capturado en ISmpTlsClient.HandshakeComplete). Despues arranca el reader task.
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

        // Llamada con _tlsLock tomado o durante el handshake (single-threaded). Drena todo
        // el output pending del TlsProtocol al socket. Toma _socketWriteLock por si alguna
        // futura task externa escribe al socket directamente (no aplica hoy, defensivo).
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

    // Marker interface para que TlsAdapterStream consulte el flag de handshake completo
    // capturado por NotifyHandshakeComplete callback.
    internal interface ISmpTlsClient
    {
        bool HandshakeComplete { get; }
    }
}
