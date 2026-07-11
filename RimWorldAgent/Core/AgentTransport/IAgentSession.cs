using System;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentTransport
{
    public interface IAgentSession : IDisposable
    {
        string? SessionId { get; }
        bool IsReady { get; }
        bool CanLoadSession { get; }
        bool CanResumeSession { get; }

        event Action? OnActivity;
        event Action<string, string?>? OnResult;
        event Func<string, string, string, Task>? OnToolUse;
        event Action? OnAborted;

        Task InitializeAsync(CancellationToken cancellationToken);
        Task NewAsync(CancellationToken cancellationToken);
        Task ResumeAsync(string sessionId, CancellationToken cancellationToken);
        Task LoadAsync(string sessionId, CancellationToken cancellationToken);
        Task PromptAsync(string prompt, CancellationToken cancellationToken);
        Task CancelAsync(CancellationToken cancellationToken);
        Task ClearAsync(CancellationToken cancellationToken);
    }
}
