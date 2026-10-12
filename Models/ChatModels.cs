namespace agentsupervisorpoc.Models;

public sealed class ChatRequest
{
    public string? SessionId { get; set; }
    public string? Message { get; set; }
    public List<string>? AttachmentIds { get; set; }
    public string? Event { get; set; }
}

public sealed record ChatResponse(
    string SessionId,
    string Content,
    string AgentName,
    int? TokensUsed = null);

public sealed record AttachmentResponse(
    string SessionId,
    string FileId,
    string Name,
    long SizeBytes,
    int ExtractedChars);
