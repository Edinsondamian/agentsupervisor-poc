using agentsupervisorpoc.Models;

namespace agentsupervisorpoc.Application.Interfaces;

public interface ISpecializedAgent
{
    string AgentName { get; }

    Task<AgentResponse> ExecuteAsync(
        string input,
        AgentContext context,
        CancellationToken cancellationToken = default);
}

public interface IAgentOrchestration
{
    Task<AgentResponse> ProcessAsync(
        string userInput,
        AgentContext context,
        IReadOnlyList<AttachmentInfo>? attachments = null,
        CancellationToken cancellationToken = default);
}

public interface IPromptProvider
{
    string Get(string agentName);

    Task WarmupAsync(CancellationToken cancellationToken = default);
}

public interface IConversationState
{
    Task<AgentContext> GetOrCreateAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(AgentContext context, CancellationToken cancellationToken = default);
}

public interface IAttachmentStore
{
    Task<AttachmentInfo> SaveAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AttachmentInfo> Resolve(IEnumerable<string?>? fileIds);
}

public interface IFoundryAgentRegistry
{
    FoundryAgentDefinition? Get(string agentName);

    IReadOnlyList<FoundryAgentDefinition> All();
}

public interface IDocumentOcrService
{
    Task<string?> ExtractTextAsync(Stream pdfContent, CancellationToken cancellationToken = default);
}
