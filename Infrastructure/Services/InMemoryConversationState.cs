using System.Collections.Concurrent;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Models;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class InMemoryConversationState : IConversationState
{
    private readonly ConcurrentDictionary<string, AgentContext> _sessions = new();

    public AgentContext GetOrCreate(string sessionId) =>
        _sessions.GetOrAdd(sessionId, id => new AgentContext
        {
            SessionId = id,
            Channel = "webchat"
        });

    public Task SaveAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        _sessions[context.SessionId] = context;
        return Task.CompletedTask;
    }
}
