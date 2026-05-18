using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Choreography.Transport.Https
{
    // Kestrel-backed HTTPS listener for the Choreography HttpsTransport.
    //
    // The Kestrel switchover replaces the previous System.Net.HttpListener
    // implementation which only ever spoke plain HTTP — its "Https" name was
    // aspirational. For Paper 7 Phase 2 we run two Stages in separate Docker
    // containers and need real TLS on the wire.
    //
    // The certificate is provided by the caller (typically generated via
    // SelfSignedCert.Generate). The peer pins this cert's fingerprint at the
    // client side (HttpsChannel / HttpClientHandler) using TOFU.
    //
    // The public surface of this class is unchanged from the previous
    // HttpListener implementation: HttpsTransport and HttpsChannel see the
    // same internal methods and lifecycle.
    internal sealed class HttpsTransportListener : IAsyncDisposable
    {
        private readonly string listenUrl;
        private readonly X509Certificate2 serverCert;
        private readonly ConcurrentDictionary<string, HttpsChannel> channels = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<(PerformerId peerId, ChannelPurpose purpose, string remoteListenUrl)>> pendingConnections = new();
        private WebApplication app;
        private CancellationTokenSource cts;

        internal HttpsTransportListener(string listenUrl, X509Certificate2 serverCert)
        {
            if (string.IsNullOrWhiteSpace(listenUrl)) throw new ArgumentNullException(nameof(listenUrl));
            if (serverCert == null) throw new ArgumentNullException(nameof(serverCert));
            if (!serverCert.HasPrivateKey) throw new ArgumentException("Server cert must have a private key for TLS", nameof(serverCert));

            this.listenUrl = listenUrl.EndsWith("/") ? listenUrl : listenUrl + "/";
            this.serverCert = serverCert;
        }

        internal void RegisterChannel(PerformerId remoteId, HttpsChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            // Key by (remoteId, ChannelPurpose) so multiple channels between
            // the same pair of peers (coord / replication / command / usher)
            // do not collide. See HttpsChannel.SendAsync for the matching
            // wire format change.
            channels[ChannelKey(remoteId, channel.Purpose)] = channel;
        }

        private static string ChannelKey(PerformerId peerId, ChannelPurpose purpose)
            => $"{peerId}:{purpose}";

        internal TaskCompletionSource<(PerformerId, ChannelPurpose, string)> CreatePendingConnection(string invitationAddress)
        {
            if (string.IsNullOrWhiteSpace(invitationAddress)) throw new ArgumentNullException(nameof(invitationAddress));
            var tcs = new TaskCompletionSource<(PerformerId, ChannelPurpose, string)>();
            pendingConnections[invitationAddress] = tcs;
            return tcs;
        }

        internal async Task StartAsync(CancellationToken ct)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Parse host + port from the listen URL. Kestrel needs the port
            // and binding mode separately (ListenAnyIP vs ListenLocalhost).
            // We bind to all interfaces unless the host is explicitly
            // "localhost" — that lets PuppeteerHost in a Docker container
            // advertise a URL like https://ordering-a:5443/ (the compose
            // network DNS name) and still bind on every interface so peers
            // can reach it. The advertised host travels into ConnectionInvitation
            // unchanged; the bind mode is independent.
            var uri = new Uri(this.listenUrl);
            int port = uri.Port;
            bool bindAny = !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();   // Stay quiet; Choreography uses its own diagnostics.
            builder.WebHost.ConfigureKestrel(opts =>
            {
                Action<ListenOptions> bind = lo => lo.UseHttps(serverCert);
                if (bindAny)
                    opts.ListenAnyIP(port, bind);
                else
                    opts.ListenLocalhost(port, bind);
            });

            app = builder.Build();

            app.MapPost("/connect", HandleConnect);
            app.MapPost("/messages", HandleMessage);

            await app.StartAsync(cts.Token).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[HttpsListener] Kestrel TLS started on {listenUrl}");
        }

        private async Task<IResult> HandleConnect(HttpRequest request)
        {
            using var reader = new StreamReader(request.Body);
            string body = await reader.ReadToEndAsync();

            string[] parts = body.Split('\n');
            if (parts.Length < 4)
                return Results.BadRequest();

            var peerId           = PerformerId.From(Convert.FromBase64String(parts[0]));
            var purpose          = (ChannelPurpose)byte.Parse(parts[1]);
            string invitationAddress = parts[2];
            string remoteListenUrl   = parts[3];

            if (pendingConnections.TryRemove(invitationAddress, out var tcs))
                tcs.TrySetResult((peerId, purpose, remoteListenUrl));

            return Results.Ok();
        }

        private async Task<IResult> HandleMessage(HttpRequest request)
        {
            byte[] data;
            using (var ms = new MemoryStream())
            {
                await request.Body.CopyToAsync(ms);
                data = ms.ToArray();
            }

            // Wire format v2: senderId(16) || purpose(1) || message(N)
            if (data.Length < 18)
                return Results.BadRequest();

            byte[] senderIdBytes = new byte[16];
            Buffer.BlockCopy(data, 0, senderIdBytes, 0, 16);
            var purpose  = (ChannelPurpose)data[16];
            var senderId = PerformerId.From(senderIdBytes);
            string senderKey = ChannelKey(senderId, purpose);

            byte[] messageBytes = new byte[data.Length - 17];
            Buffer.BlockCopy(data, 17, messageBytes, 0, messageBytes.Length);

            if (channels.TryGetValue(senderKey, out var channel))
            {
                try
                {
                    var message = StageMessage.Deserialize(messageBytes);
                    channel.EnqueueReceived(message);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HttpsListener] Failed to deserialize message from {senderKey}: {ex.Message}");
                }
            }

            return Results.Ok();
        }

        public async ValueTask DisposeAsync()
        {
            try { cts?.Cancel(); } catch { }
            if (app != null)
            {
                try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                try { await app.DisposeAsync(); } catch { }
            }
            cts?.Dispose();
        }
    }
}
