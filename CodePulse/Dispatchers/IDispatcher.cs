using CodePulse.Models;

namespace CodePulse.Dispatchers;

public interface IDispatcher
{
    Task DispatchAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken);
}
