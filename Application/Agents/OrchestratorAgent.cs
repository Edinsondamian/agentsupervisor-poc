using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Models;
using AgentReply = agentsupervisorpoc.Models.AgentResponse;

namespace agentsupervisorpoc.Application.Agents;

public sealed class OrchestratorAgent : ISpecializedAgent
{
    public string AgentName => "OrchestratorAgent";

    private readonly AgentFactory _factory;
    private readonly ILogger<OrchestratorAgent> _logger;

    public OrchestratorAgent(AgentFactory factory, ILogger<OrchestratorAgent> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<AgentReply> ExecuteAsync(
        string input,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var agent = _factory.BuildAgent(AgentName);

            var session = await RestoreOrCreateSessionAsync(agent, context, cancellationToken);

            var response = await agent.RunAsync(input, session, cancellationToken: cancellationToken);

            var bag = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
            context.AgentSessionState[AgentName] = bag.ToString();

            sw.Stop();
            _logger.LogInformation(
                "[{Agent}] Respondió en {Elapsed} ms. SessionId={SessionId}",
                AgentName, sw.ElapsedMilliseconds, context.SessionId);

            var text = response.ToString();
            return new AgentReply(text, AgentName, NeedsMoreInput: text.Contains('?'));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{Agent}] Falló. SessionId={SessionId}",
                AgentName, context.SessionId);
            throw new InvalidOperationException(
                $"[{AgentName}] Orchestrator execution failed for SessionId={context.SessionId}", ex);
        }
    }

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        ChatClientAgent agent,
        AgentContext context,
        CancellationToken ct)
    {
        if (context.AgentSessionState.TryGetValue(AgentName, out var serialized))
        {
            var json = System.Text.Json.JsonDocument.Parse(serialized);
            return await agent.DeserializeSessionAsync(json.RootElement, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }
}
