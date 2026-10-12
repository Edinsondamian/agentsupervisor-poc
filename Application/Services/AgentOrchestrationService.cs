using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using agentsupervisorpoc.Application.Agents;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Models;

namespace agentsupervisorpoc.Application.Services;

public sealed class AgentOrchestrationService : IAgentOrchestration
{
    private const int MaxInputLength = 4000;
    private const int MaxThreadMessages = 20;

    private static readonly Regex CreditValidationPattern = new(
        @"\b(aprueb\w*|aprobaci[oó]n|deneg\w*|rechaz\w*|veredicto)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly OrchestratorAgent _orchestrator;
    private readonly IConversationState _conversationState;
    private readonly IFoundryAgentRegistry _foundryAgents;
    private readonly ILogger<AgentOrchestrationService> _logger;
    private readonly string _errorGeneral;

    public AgentOrchestrationService(
        OrchestratorAgent orchestrator,
        IConversationState conversationState,
        IFoundryAgentRegistry foundryAgents,
        IConfiguration configuration,
        ILogger<AgentOrchestrationService> logger)
    {
        _orchestrator = orchestrator;
        _conversationState = conversationState;
        _foundryAgents = foundryAgents;
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

            userInput = "(El usuario envió este mensaje únicamente con archivos PDF adjuntos, sin texto adicional.)";
        }

        if (userInput.Length > MaxInputLength)
            return new AgentResponse(
                "Tu mensaje es demasiado largo. Por favor, resúmelo en menos de 4000 caracteres.",
                "ValidationPipeline");

        try
        {
            LogFoundryRouting(userInput, attachments);

            var agentInput = BuildAgentInput(userInput, attachments);
            var response = await _orchestrator.ExecuteAsync(agentInput, context, cancellationToken);

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

    private void LogFoundryRouting(string userInput, IReadOnlyList<AttachmentInfo>? attachments)
    {
        var route = ResolveFoundryRoute(userInput, attachments);
        if (route is null)
            return;

        var agent = _foundryAgents.Get(route);
        _logger.LogInformation(
            "[Routing] Turno candidato al agente Foundry '{Route}' ({Description}). Configurado={IsConfigured}; por ahora se responde con el orquestador NLP local.",
            route,
            agent?.Description,
            agent?.IsConfigured ?? false);
    }

    private static string? ResolveFoundryRoute(string userInput, IReadOnlyList<AttachmentInfo>? attachments)
    {
        if (attachments is { Count: > 0 })
            return FoundryAgentRegistry.DocumentationEvaluationAgent;

        if (CreditValidationPattern.IsMatch(userInput))
            return FoundryAgentRegistry.CreditValidationAgent;

        return null;
    }

    private static string BuildAgentInput(string userInput, IReadOnlyList<AttachmentInfo>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return userInput;

        var sb = new StringBuilder(userInput.TrimEnd());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"[CONTEXTO: el usuario adjuntó {attachments.Count} archivo(s) PDF en este mensaje]");
        foreach (var a in attachments)
        {
            sb.AppendLine($"- {a.Name} ({a.SizeBytes / 1024.0:F0} KB)");
            if (!string.IsNullOrWhiteSpace(a.ExtractedText))
            {
                sb.AppendLine("Contenido extraído del PDF (puede estar truncado):");
                sb.AppendLine("\"\"\"");
                sb.AppendLine(a.ExtractedText);
                sb.AppendLine("\"\"\"");
            }
            else
            {
                sb.AppendLine("(No se pudo extraer texto del PDF: puede ser un documento escaneado o sin texto).");
            }
        }
        sb.Append("Usa el contenido de los adjuntos, junto con los datos de la conversación, para atender el mensaje del usuario.");
        return sb.ToString();
    }

    private static string DescribeForThread(string userInput, IReadOnlyList<AttachmentInfo>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return userInput;

        var names = string.Join(", ", attachments.Select(a => a.Name));
        return $"{userInput} [PDF adjunto: {names}]";
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
