using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.ApiClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Networking
{
    /// <summary>
    /// <see cref="BackgroundService"/> responsible for responding to auto-discovery messages.
    /// </summary>
    public sealed class AutoDiscoveryHost : BackgroundService
    {
        private const int PortNumber = 7359;

        private readonly ILogger<AutoDiscoveryHost> _logger;
        private readonly IServerApplicationHost _appHost;
        private readonly IConfigurationManager _configurationManager;
        private readonly INetworkManager _networkManager;

        public AutoDiscoveryHost(
            ILogger<AutoDiscoveryHost> logger,
            IServerApplicationHost appHost,
            IConfigurationManager configurationManager,
            INetworkManager networkManager)
        {
            _logger = logger;
            _appHost = appHost;
            _configurationManager = configurationManager;
            _networkManager = networkManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var networkConfig = _configurationManager.GetNetworkConfiguration();
            if (!networkConfig.AutoDiscovery)
            {
                _logger.LogInformation("Auto-discovery is disabled in the configuration.");
                return;
            }

            await ListenForAutoDiscoveryMessage(IPAddress.Any, stoppingToken).ConfigureAwait(false);
        }

        private async Task ListenForAutoDiscoveryMessage(IPAddress listenAddress, CancellationToken cancellationToken)
        {
            try
            {
                using var udpClient = new UdpClient(new IPEndPoint(listenAddress, PortNumber));
                udpClient.MulticastLoopback = false;
                udpClient.Client.ReceiveTimeout = 5000; // Set a receive timeout (in milliseconds)

                _logger.LogInformation("Listening for auto-discovery messages on {Address}:{Port}", listenAddress, PortNumber);

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                        var text = Encoding.UTF8.GetString(result.Buffer);
                        _logger.LogTrace("Received message: {Message}", text);

                        if (text.Contains("who is JellyfinServer?", StringComparison.OrdinalIgnoreCase))
                        {
                            await RespondToV2Message(result.RemoteEndPoint, udpClient, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogError(ex, "Socket exception while receiving data");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Auto-discovery service operation canceled.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the UDP listener on {Address}:{Port}", listenAddress, PortNumber);
            }
        }

        private async Task RespondToV2Message(IPEndPoint endpoint, UdpClient udpClient, CancellationToken cancellationToken)
        {
            var localUrl = _appHost.GetSmartApiUrl(endpoint.Address);
            if (string.IsNullOrEmpty(localUrl))
            {
                _logger.LogWarning("Unable to respond to server discovery request. Local IP address could not be determined.");
                return;
            }

            var response = new ServerDiscoveryInfo(localUrl, _appHost.SystemId, _appHost.FriendlyName);

            try
            {
                var responseData = JsonSerializer.SerializeToUtf8Bytes(response).AsMemory();
                _logger.LogDebug("Sending AutoDiscovery response to {EndPoint}", endpoint);
                await udpClient.SendAsync(responseData, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Error sending response message");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during AutoDiscovery response");
            }
        }
    }
}
