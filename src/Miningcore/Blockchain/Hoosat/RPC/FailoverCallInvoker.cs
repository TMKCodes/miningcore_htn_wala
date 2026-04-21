using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;

namespace Miningcore.Blockchain.Hoosat.RPC;

/// <summary>
/// A gRPC CallInvoker that distributes calls across multiple endpoints (round-robin)
/// and retries unary calls on transient transport failures.
/// 
/// This is primarily used to add multi-daemon support for Hoosat htnd/walletd gRPC endpoints.
/// </summary>
public sealed class FailoverCallInvoker : CallInvoker
{
  public FailoverCallInvoker(CallInvoker[] invokers)
  {
    if (invokers == null || invokers.Length == 0)
      throw new ArgumentException("At least one CallInvoker must be provided", nameof(invokers));

    if (invokers.Any(x => x == null))
      throw new ArgumentException("CallInvoker list contains null", nameof(invokers));

    this.invokers = invokers;
  }

  private readonly CallInvoker[] invokers;
  private int nextIndex;

  private CallInvoker GetNext()
  {
    var idx = Interlocked.Increment(ref nextIndex);
    if (idx == int.MaxValue)
      Interlocked.Exchange(ref nextIndex, 0);

    return invokers[Math.Abs(idx) % invokers.Length];
  }

  private static bool IsTransient(RpcException ex)
  {
    // Typical transient conditions for node failover.
    return ex.StatusCode == StatusCode.Unavailable ||
           ex.StatusCode == StatusCode.DeadlineExceeded ||
           ex.StatusCode == StatusCode.ResourceExhausted;
  }

  public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method,
      string host,
      CallOptions options,
      TRequest request)
  {
    // Implement retries at the ResponseAsync level so call sites that do `await call.ResponseAsync`
    // transparently failover.
    var cts = options.CancellationToken;

    AsyncUnaryCall<TResponse> lastCall = null;

    async Task<TResponse> ResponseAsync()
    {
      Exception lastError = null;

      for (var attempt = 0; attempt < invokers.Length; attempt++)
      {
        cts.ThrowIfCancellationRequested();

        var invoker = GetNext();
        var call = invoker.AsyncUnaryCall(method, host, options, request);
        lastCall = call;

        try
        {
          return await call.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex) when (attempt < invokers.Length - 1 && IsTransient(ex))
        {
          lastError = ex;
          try { call.Dispose(); } catch { /* ignore */ }
        }
        catch (Exception ex) when (attempt < invokers.Length - 1)
        {
          // For non-RPC exceptions (e.g. HttpRequestException wrapped), allow failover.
          lastError = ex;
          try { call.Dispose(); } catch { /* ignore */ }
        }
      }

      throw lastError ?? new RpcException(new Status(StatusCode.Unavailable, "All daemon endpoints failed"));
    }

    // Best-effort headers/status/trailers: use the most recent inner call if available.
    Task<Metadata> ResponseHeadersAsync() => lastCall?.ResponseHeadersAsync ?? Task.FromResult(new Metadata());
    Status GetStatus() => lastCall?.GetStatus() ?? new Status(StatusCode.Unknown, "No call executed");
    Metadata GetTrailers() => lastCall?.GetTrailers() ?? new Metadata();
    void Dispose() { try { lastCall?.Dispose(); } catch { /* ignore */ } }

    return new AsyncUnaryCall<TResponse>(ResponseAsync(), ResponseHeadersAsync(), GetStatus, GetTrailers, Dispose);
  }

  public override TResponse BlockingUnaryCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method,
      string host,
      CallOptions options,
      TRequest request)
  {
    Exception lastError = null;

    for (var attempt = 0; attempt < invokers.Length; attempt++)
    {
      options.CancellationToken.ThrowIfCancellationRequested();

      var invoker = GetNext();

      try
      {
        return invoker.BlockingUnaryCall(method, host, options, request);
      }
      catch (RpcException ex) when (attempt < invokers.Length - 1 && IsTransient(ex))
      {
        lastError = ex;
      }
      catch (Exception ex) when (attempt < invokers.Length - 1)
      {
        lastError = ex;
      }
    }

    if (lastError is RpcException rex)
      throw rex;

    throw lastError ?? new RpcException(new Status(StatusCode.Unavailable, "All daemon endpoints failed"));
  }

  public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method,
      string host,
      CallOptions options,
      TRequest request)
  {
    // One endpoint per stream. Call sites already contain reconnect loops; the next stream
    // creation will round-robin to a different endpoint.
    return GetNext().AsyncServerStreamingCall(method, host, options, request);
  }

  public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method,
      string host,
      CallOptions options)
  {
    return GetNext().AsyncClientStreamingCall(method, host, options);
  }

  public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method,
      string host,
      CallOptions options)
  {
    return GetNext().AsyncDuplexStreamingCall(method, host, options);
  }
}

/// <summary>
/// Minimal ChannelBase wrapper that provides a failover CallInvoker.
/// Generated gRPC clients in this repo accept ChannelBase (not CallInvoker).
/// </summary>
public sealed class FailoverChannel : ChannelBase
{
  public FailoverChannel(string target, CallInvoker[] invokers) : base(target)
  {
    failoverInvoker = new FailoverCallInvoker(invokers);
  }

  private readonly FailoverCallInvoker failoverInvoker;

  public override CallInvoker CreateCallInvoker() => failoverInvoker;
}
