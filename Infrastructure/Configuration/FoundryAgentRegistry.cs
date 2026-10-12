using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace agentsupervisorpoc.Infrastructure.Configuration;

public sealed class FoundryAgentRegistry : IFoundryAgentRegistry
{
    public const string DocumentationEvaluationAgent = "DocumentationEvaluationAgent";
    public const string CreditValidationAgent = "CreditValidationAgent";

    private readonly Dictionary<string, FoundryAgentDefinition> _agents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<FoundryAgentRegistry> _logger;

    public FoundryAgentRegistry(IConfiguration config, ILogger<FoundryAgentRegistry> logger)
    {
        _logger = logger;

        foreach (var name in new[] { DocumentationEvaluationAgent, CreditValidationAgent })
        {
            var section = config.GetSection($"FoundryAgents:{name}");
            _agents[name] = new FoundryAgentDefinition
            {
                Name = name,
                AgentId = section["AgentId"] ?? string.Empty,
                Description = section["Description"] ?? string.Empty
            };
        }
    }

    public FoundryAgentDefinition? Get(string agentName) =>
        _agents.TryGetValue(agentName, out var agent) ? agent : null;

    public IReadOnlyList<FoundryAgentDefinition> All() => _agents.Values.ToList();

    public void LogStatus()
    {
        foreach (var agent in All())
        {
            _logger.LogInformation(
                "[Foundry] Agente '{Name}': {Description} AgentId={AgentId}",
                agent.Name,
                agent.Description,
                agent.IsConfigured ? "configurado" : "pendiente de configurar");
        }
    }
}
