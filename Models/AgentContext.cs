namespace agentsupervisorpoc.Models;

public sealed class AgentContext
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string Channel { get; init; } = "webchat";

    public Dictionary<string, string> AgentSessionState { get; init; } = new();

    public List<DomainMessage> ConversationThread { get; init; } = new();
}

public sealed class DomainMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? AgentName { get; set; }

    public DomainMessage() { }

    public DomainMessage(string role, string content, string? agentName = null)
    {
        Role = role;
        Content = content;
        AgentName = agentName;
    }
}
