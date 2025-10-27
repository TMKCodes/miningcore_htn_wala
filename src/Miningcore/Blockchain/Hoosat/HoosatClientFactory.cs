using System.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using System.Net;
using System.Threading.Tasks;
using Miningcore.Blockchain.Hoosat.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using NLog;
using Microsoft.Extensions.Logging;
using htnWalletd = Miningcore.Blockchain.Hoosat.HtnWalletd;
using htnd = Miningcore.Blockchain.Hoosat.Htnd;

namespace Miningcore.Blockchain.Hoosat;

using Microsoft.Extensions.Logging;
using Grpc.Net.Client;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public static class HoosatClientFactory
{
    public static htnd.HtndRPC.HtndRPCClient CreateHtndRPCClient(DaemonEndpointConfig[] daemonEndpoints, string protobufDaemonRpcServiceName)
    {
        var daemonEndpoint = daemonEndpoints.First();

        var baseUrl = new UriBuilder(daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug); 
            builder.AddFilter("Grpc.Net.Client", LogLevel.Debug);
            builder.AddFilter("Grpc.Net.Client.Internal.GrpcCall", LogLevel.Debug);
        });

        var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions()
        {
            HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            },
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 2097152, // 2MB
            MaxSendMessageSize = 2097152, // 2MB
        });

        return new htnd.HtndRPC.HtndRPCClient(new htnd.HtndRPC(protobufDaemonRpcServiceName), channel);
    }

    public static htnWalletd.HtnWalletdRPC.HtnWalletdRPCClient CreateHtnWalletdRPCClient(DaemonEndpointConfig[] daemonEndpoints, string protobufWalletRpcServiceName)
    {
        var daemonEndpoint = daemonEndpoints.First();

        var baseUrl = new UriBuilder(daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("Grpc.Net.Client", LogLevel.Debug);
            builder.AddFilter("Grpc.Net.Client.Internal.GrpcCall", LogLevel.Debug);
        });

        var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions()
        {
            HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            },
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 2097152, // 2MB
            MaxSendMessageSize = 2097152, // 2MB
        });

        return new htnWalletd.HtnWalletdRPC.HtnWalletdRPCClient(new htnWalletd.HtnWalletdRPC(protobufWalletRpcServiceName), channel);
    }
}
