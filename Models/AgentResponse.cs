namespace agentsupervisorpoc.Models;

public sealed record AgentResponse(
    string Content,
    string AgentName,
    int? TokensUsed = null,
    bool NeedsMoreInput = false);
