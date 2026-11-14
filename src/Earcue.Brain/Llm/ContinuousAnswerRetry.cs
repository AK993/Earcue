namespace Earcue.Brain.Llm;

/// <summary>Retries a whole answer two seconds after each failure, until success or cancellation.</summary>
public sealed class ContinuousAnswerRetry
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ContinuousAnswerRetry(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));

    public async Task RunAsync(Func<int, CancellationToken, Task> answer,
        Action<Exception, int, TimeSpan> onRetry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(onRetry);
        var retries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await answer(retries, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                retries++;
                var delay = TimeSpan.FromSeconds(2);
                onRetry(error, retries, delay);
                await _delay(delay, cancellationToken);
            }
        }
    }
}
