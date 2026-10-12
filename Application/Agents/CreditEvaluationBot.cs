using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.Compat;
using Microsoft.Agents.Core.Models;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Application.Services;
using agentsupervisorpoc.Infrastructure.Services;
using agentsupervisorpoc.Models;

namespace agentsupervisorpoc.Application.Agents;

public sealed class CreditEvaluationBot : ActivityHandler
{
    private readonly IAgentOrchestration _orchestration;
    private readonly IConversationState _state;
    private readonly IAttachmentStore _attachments;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CreditEvaluationBot> _logger;

    public CreditEvaluationBot(
        IAgentOrchestration orchestration,
        IConversationState state,
        IAttachmentStore attachments,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CreditEvaluationBot> logger)
    {
        _orchestration = orchestration;
        _state = state;
        _attachments = attachments;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken = default)
    {
        var userInput = turnContext.Activity.Text?.Trim();
        var sessionId = turnContext.Activity.Conversation?.Id;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("[Bot] Mensaje sin Conversation.Id");
            return;
        }

        IReadOnlyList<AttachmentInfo>? attachments = null;

        if (turnContext.Activity.Attachments is { Count: > 0 })
        {
            var resolved = await ResolveActivityAttachmentsAsync(
                sessionId, turnContext, turnContext.Activity.Attachments, cancellationToken);

            if (resolved is null)
                return;

            attachments = resolved;
        }

        if (string.IsNullOrWhiteSpace(userInput) && attachments is null)
        {
            _logger.LogWarning("[Bot] Mensaje sin texto ni documentos. ConversationId={ConversationId}", sessionId);
            return;
        }

        var context = await _state.GetOrCreateAsync(sessionId, cancellationToken);

        try
        {
            var response = await _orchestration.ProcessAsync(
                userInput ?? string.Empty, context, attachments, cancellationToken);

            if (!string.IsNullOrEmpty(response.Content))
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(response.Content), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Bot] Error procesando mensaje. SessionId={SessionId}", sessionId);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(_configuration["MensajesError:ErrorGeneral"]
                    ?? "Uy, parece que algo no funcionó como esperábamos. Por favor, inténtalo nuevamente."),
                cancellationToken);
        }
    }

    private async Task<IReadOnlyList<AttachmentInfo>?> ResolveActivityAttachmentsAsync(
        string sessionId,
        ITurnContext turnContext,
        IList<Attachment> activityAttachments,
        CancellationToken cancellationToken)
    {
        var resolved = new List<AttachmentInfo>();
        var errors = new List<string>();

        foreach (var attachment in activityAttachments)
        {
            var name = string.IsNullOrWhiteSpace(attachment.Name) ? "documento" : attachment.Name;

            if (!IsSupportedAttachment(name, attachment.ContentType))
                continue;

            try
            {
                using var content = await DownloadAttachmentAsync(attachment, cancellationToken);
                if (content is null)
                {
                    errors.Add($"No se pudo leer el documento {name}.");
                    continue;
                }

                resolved.Add(await _attachments.SaveAsync(sessionId, name, content, cancellationToken));
            }
            catch (InvalidDataException ex)
            {
                errors.Add(ex.Message);
            }
            catch (Exception)
            {
                errors.Add($"No se pudo procesar el documento {name}.");
            }
        }

        var validationError = AttachmentStore.ValidateForMessage(resolved);
        if (validationError is not null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(validationError), cancellationToken);
            return null;
        }

        if (resolved.Count == 0)
        {
            if (errors.Count > 0)
            {
                _logger.LogWarning("[Bot] Documentos rechazados: {Errors}", string.Join(" | ", errors));
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(errors[0]), cancellationToken);
            }
            return Array.Empty<AttachmentInfo>();
        }

        if (errors.Count > 0)
            _logger.LogWarning("[Bot] Documentos descartados: {Errors}", string.Join(" | ", errors));

        return resolved;
    }

    private static bool IsSupportedAttachment(string name, string? contentType)
    {
        var isPdf = string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        var isImage = string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        return isPdf || isImage;
    }

    private async Task<Stream?> DownloadAttachmentAsync(
        Attachment attachment,
        CancellationToken cancellationToken)
    {
        var url = attachment.ContentUrl;
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0)
                return null;

            var bytes = Convert.FromBase64String(url[(comma + 1)..]);
            return new MemoryStream(bytes);
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        var http = _httpClientFactory.CreateClient();
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new MemoryStream(content);
    }

    protected override async Task OnEventActivityAsync(
        ITurnContext<IEventActivity> turnContext,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(turnContext.Activity.Name, "webchat/join", StringComparison.OrdinalIgnoreCase))
            return;

        await turnContext.SendActivityAsync(
            MessageFactory.Text(WelcomeService.Build(_configuration)), cancellationToken);
    }

    protected override Task OnMembersAddedAsync(
        IList<ChannelAccount> membersAdded,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
