using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using agentsupervisorpoc.Application.Agents;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Infrastructure.Foundry;
using agentsupervisorpoc.Models;

namespace agentsupervisorpoc.Application.Services;

public sealed class AgentOrchestrationService : IAgentOrchestration
{
    private const int MaxInputLength = 4000;
    private const int MaxThreadMessages = 20;

    private readonly OrchestratorAgent _orchestrator;
    private readonly IConversationState _conversationState;
    private readonly IFoundryAgentRegistry _foundryAgents;
    private readonly FoundryAgentClient _foundryAgentClient;
    private readonly ILogger<AgentOrchestrationService> _logger;
    private readonly string _errorGeneral;

    public AgentOrchestrationService(
        OrchestratorAgent orchestrator,
        IConversationState conversationState,
        IFoundryAgentRegistry foundryAgents,
        FoundryAgentClient foundryAgentClient,
        IConfiguration configuration,
        ILogger<AgentOrchestrationService> logger)
    {
        _orchestrator = orchestrator;
        _conversationState = conversationState;
        _foundryAgents = foundryAgents;
        _foundryAgentClient = foundryAgentClient;
        _logger = logger;
        _errorGeneral = configuration["MensajesError:ErrorGeneral"]
            ?? "Uy, parece que algo no funcionó como esperábamos. Por favor, inténtalo nuevamente. Estoy aquí para ayudarte.";
    }

    public async Task<AgentResponse> ProcessAsync(
        string userInput,
        AgentContext context,
        IReadOnlyList<AttachmentInfo>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var hasAttachments = attachments is { Count: > 0 };

        if (string.IsNullOrWhiteSpace(userInput))
        {
            if (!hasAttachments)
                return new AgentResponse(
                    "Por favor, escribe tu consulta para poder ayudarte.", "ValidationPipeline");

            userInput = "(El usuario envió este mensaje únicamente con documentos adjuntos, sin texto adicional.)";
        }

        if (userInput.Length > MaxInputLength)
            return new AgentResponse(
                "Tu mensaje es demasiado largo. Por favor, resúmelo en menos de 4000 caracteres.",
                "ValidationPipeline");

        try
        {
            AgentResponse? response = null;

            if (hasAttachments)
            {
                var content = BuildDocumentContent(attachments!);
                var direct = await TryRunDocumentEvaluationAsync(content, cancellationToken);

                if (direct is not null)
                {
                    context.LastDocumentContent = content;
                    response = direct;
                }
            }

            response ??= await _orchestrator.ExecuteAsync(
                BuildAgentInput(userInput, attachments, context), context, cancellationToken);

            AppendThread(context, DescribeForThread(userInput, attachments), response);
            await _conversationState.SaveAsync(context, cancellationToken);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Agent pipeline failed for session {SessionId}", context.SessionId);
            return new AgentResponse(_errorGeneral, "OrchestrationPipeline");
        }
    }

    private static string BuildDocumentContent(IReadOnlyList<AttachmentInfo> attachments)
    {
        var sb = new StringBuilder();
        foreach (var a in attachments)
        {
            sb.AppendLine($"Documento: {a.Name}");
            sb.AppendLine(a.ExtractedText ?? "(sin texto extraído)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<AgentResponse?> TryRunDocumentEvaluationAsync(
        string documentContent,
        CancellationToken cancellationToken)
    {
        var agent = _foundryAgents.Get(FoundryAgentRegistry.DocumentationEvaluationAgent);
        if (agent is null || !agent.IsConfigured)
            return null;

        try
        {
            var output = await _foundryAgentClient.RunAsync(agent.AgentId, documentContent, cancellationToken);

            return string.IsNullOrWhiteSpace(output)
                ? null
                : new AgentResponse(output, agent.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Documentacion] Falló la evaluación documental directa; se responde con el orquestador.");
            return null;
        }
    }

    private string BuildAgentInput(
        string userInput,
        IReadOnlyList<AttachmentInfo>? attachments,
        AgentContext context)
    {
        if (attachments is { Count: > 0 })
        {
            var sb = new StringBuilder(userInput.TrimEnd());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"[CONTEXTO: el usuario adjuntó {attachments.Count} documento(s) en este mensaje]");
            foreach (var a in attachments)
            {
                sb.AppendLine($"- {a.Name} ({a.SizeBytes / 1024.0:F0} KB)");
                if (!string.IsNullOrWhiteSpace(a.ExtractedText))
                {
                    sb.AppendLine("Contenido extraído del documento (puede estar truncado):");
                    sb.AppendLine("\"\"\"");
                    sb.AppendLine(a.ExtractedText);
                    sb.AppendLine("\"\"\"");
                }
                else
                {
                    sb.AppendLine("(No se pudo extraer texto del documento: puede ser un documento escaneado o sin texto).");
                }
            }
            sb.Append("Usa el contenido de los adjuntos, junto con los datos de la conversación, para atender el mensaje del usuario.");
            return sb.ToString();
        }

        var previous = FindLastDocumentEvaluation(context);
        if (previous is null && string.IsNullOrWhiteSpace(context.LastDocumentContent))
            return userInput;

        var contextSb = new StringBuilder();

        if (previous is not null)
        {
            contextSb.AppendLine("[Evaluación documental previa del agente de Foundry]");
            contextSb.AppendLine(previous.Content);
            contextSb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(context.LastDocumentContent))
        {
            contextSb.AppendLine("[Contenido del último documento evaluado]");
            contextSb.AppendLine(context.LastDocumentContent);
            contextSb.AppendLine();
        }

        contextSb.Append(userInput);
        return contextSb.ToString();
    }

    private DomainMessage? FindLastDocumentEvaluation(AgentContext context)
    {
        var agent = _foundryAgents.Get(FoundryAgentRegistry.DocumentationEvaluationAgent);
        if (agent is null)
            return null;

        for (var i = context.ConversationThread.Count - 1; i >= 0; i--)
        {
            var message = context.ConversationThread[i];
            if (message.Role == "assistant"
                && string.Equals(message.AgentName, agent.AgentId, StringComparison.OrdinalIgnoreCase))
                return message;
        }

        return null;
    }

    private static string DescribeForThread(string userInput, IReadOnlyList<AttachmentInfo>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return userInput;

        var names = string.Join(", ", attachments.Select(a => a.Name));
        return $"{userInput} [Documento adjunto: {names}]";
    }

    private static void AppendThread(AgentContext context, string userInput, AgentResponse response)
    {
        context.ConversationThread.Add(new DomainMessage("user", userInput));
        if (!string.IsNullOrEmpty(response.Content))
            context.ConversationThread.Add(new DomainMessage("assistant", response.Content, response.AgentName));

        while (context.ConversationThread.Count > MaxThreadMessages)
            context.ConversationThread.RemoveAt(0);
    }
}
