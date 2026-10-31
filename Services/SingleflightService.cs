namespace SemanticCachingGateway.Services;

using System.Collections.Concurrent;

public interface ISingleflightService
{
    Task<string> ExecuteAsync(string key, Func<Task<string>> action, TimeSpan? timeout = null);
    int ActiveInvocations { get; }
}

public class SingleflightService : ISingleflightService
{
    private class Call
    {
        public Task<string>? Task { get; set; }
    }

    private readonly ConcurrentDictionary<string, Call> _calls = new();
    private int _invocationCount = 0;
    public int ActiveInvocations => _invocationCount;

    public async Task<string> ExecuteAsync(string key, Func<Task<string>> action, TimeSpan? timeout = null)
    {
        var callTimeout = timeout ?? TimeSpan.FromSeconds(15);
        Call call;
        bool isLeader = false;

        lock (_calls)
        {
            if (_calls.TryGetValue(key, out var existing))
            {
                call = existing;
            }
            else
            {
                call = new Call();
                _calls[key] = call;
                isLeader = true;
            }
        }

        if (isLeader)
        {
            Interlocked.Increment(ref _invocationCount);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            call.Task = tcs.Task;

            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(callTimeout);
                try
                {
                    string result = await action();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    lock (_calls)
                    {
                        _calls.TryRemove(key, out _);
                    }
                }
            });
        }

        // Both leader and followers await the single in-flight task
        return await call.Task!.WaitAsync(callTimeout);
    }
}
