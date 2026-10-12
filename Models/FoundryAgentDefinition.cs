namespace agentsupervisorpoc.Models;

public sealed class FoundryAgentDefinition
{
    public string Name { get; init; } = string.Empty;
    public string AgentId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AgentId);
}
