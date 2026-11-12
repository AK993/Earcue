using Earcue.Core.Models;

namespace Earcue.Core.Interfaces;

public interface ILlmClient
{
    IAsyncEnumerable<string> AskAsync(Prompt prompt, CancellationToken cancellationToken = default);
}
