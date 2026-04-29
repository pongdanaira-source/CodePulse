namespace CodePulse.Services;

public interface IHiddenWebViewHost
{
    event Action<string>? ObserverMessageReceived;
    event Action<string>? NavigationFailed;
    event Action<string>? BrowserProcessFailed;
    event Action<string>? DebugLogEmitted;

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<bool> NavigateAndWaitUntilReadyAsync(string chatLink, CancellationToken cancellationToken);

    Task<bool> ReloadAndWaitUntilReadyAsync(CancellationToken cancellationToken);

    string? DocumentTitle { get; }

    void DisposeHost();
}
