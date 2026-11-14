using System.Net;
using Earcue.Brain.Llm;

namespace Earcue.Brain.Tests;

public class ContinuousAnswerRetryTests
{
    [Fact]
    public async Task RepeatedFailuresEventuallySucceedWithoutAnAttemptLimit()
    {
        var delays = new List<double>();
        var runner = new ContinuousAnswerRetry((delay, ct) => { delays.Add(delay.TotalSeconds); return Task.CompletedTask; });
        var calls = 0;
        await runner.RunAsync((attempt, ct) =>
        {
            calls++;
            if (calls <= 8) throw new TimeoutException("首字超时");
            return Task.CompletedTask;
        }, (error, attempt, delay) => Assert.IsType<TimeoutException>(error));
        Assert.Equal(9, calls);
        Assert.Equal(Enumerable.Repeat(2.0, 8), delays);
    }

    [Fact]
    public async Task FirstSuccessDoesNotRetry()
    {
        var calls = 0;
        var runner = new ContinuousAnswerRetry((delay, ct) => throw new Exception("不应等待"));
        await runner.RunAsync((attempt, ct) => { calls++; return Task.CompletedTask; }, (_, _, _) => throw new Exception("不应重试"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallerCancellationStopsDuringBackoff()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runner = new ContinuousAnswerRetry(async (delay, ct) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync((_, _) => { calls++; throw new HttpRequestException("断网"); }, (_, _, _) => { }, cts.Token);
        await waiting.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationDuringRequestDoesNotScheduleAnotherAttempt()
    {
        using var cts = new CancellationTokenSource();
        var runner = new ContinuousAnswerRetry();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync((_, ct) =>
        {
            cts.Cancel();
            return Task.FromCanceled(ct);
        }, (_, _, _) => throw new Exception("取消后不应重试"), cts.Token));
    }

    [Fact]
    public async Task ExplicitStopCanEndRepeatedAuthenticationFailures()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var runner = new ContinuousAnswerRetry((delay, ct) => Task.Delay(1, ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync((_, _) =>
        {
            attempts++;
            throw new HttpRequestException("鉴权失败", null, HttpStatusCode.Unauthorized);
        }, (_, count, _) => { if (count == 5) cts.Cancel(); }, cts.Token));
        Assert.Equal(5, attempts);
    }

    [Fact]
    public async Task PreCanceledCallerDoesNotStartRequest()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ContinuousAnswerRetry().RunAsync(
            (_, _) => throw new Exception("不应请求"), (_, _, _) => { }, cts.Token));
    }
}
