using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Hoosat.RPC;
using Miningcore.Configuration;
using htnWalletd = Miningcore.Blockchain.Hoosat.HtnWalletd;
using htnd = Miningcore.Blockchain.Hoosat.Htnd;

namespace Miningcore.Blockchain.Hoosat;

public static class HoosatClientFactory
{
    public static htnd.HtndRPC.HtndRPCClient CreateHtndRPCClient(DaemonEndpointConfig[] daemonEndpoints, string protobufDaemonRpcServiceName)
    {
        if (daemonEndpoints == null || daemonEndpoints.Length == 0)
            throw new ArgumentException("No daemon endpoints configured", nameof(daemonEndpoints));

        var invokers = daemonEndpoints
            .Select(daemonEndpoint =>
            {
                var baseUrl = new UriBuilder(daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                    daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);

                var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                        KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                        EnableMultipleHttp2Connections = true,
                    },
                    DisposeHttpClient = true,
                    MaxReceiveMessageSize = 2097152,
                    MaxSendMessageSize = 2097152,
                });

                return channel.CreateCallInvoker();
            })
            .ToArray();

        var failoverChannel = new FailoverChannel("htnd-failover", invokers);
        return new htnd.HtndRPC.HtndRPCClient(new htnd.HtndRPC(protobufDaemonRpcServiceName), failoverChannel);
    }

    public static htnWalletd.HtnWalletdRPC.HtnWalletdRPCClient CreateHtnWalletdRPCClient(DaemonEndpointConfig[] daemonEndpoints, string protobufWalletRpcServiceName)
    {
        if (daemonEndpoints == null || daemonEndpoints.Length == 0)
            throw new ArgumentException("No wallet daemon endpoints configured", nameof(daemonEndpoints));

        var invokers = daemonEndpoints
            .Select(daemonEndpoint =>
            {
                var baseUrl = new UriBuilder(daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                    daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);

                var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                        KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                        EnableMultipleHttp2Connections = true,
                    },
                    DisposeHttpClient = true,
                    MaxReceiveMessageSize = 2097152,
                    MaxSendMessageSize = 2097152,
                });

                return channel.CreateCallInvoker();
            })
            .ToArray();

        var failoverChannel = new FailoverChannel("walletd-failover", invokers);
        return new htnWalletd.HtnWalletdRPC.HtnWalletdRPCClient(new htnWalletd.HtnWalletdRPC(protobufWalletRpcServiceName), failoverChannel);
    }
}
