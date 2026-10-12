using System.Collections.Concurrent;
using Azure.Storage.Blobs;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class BlobConversationState : IConversationState
{
    private readonly ConcurrentDictionary<string, AgentContext> _sessions = new();
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobConversationState> _logger;
    private Task? _initTask;

    public BlobConversationState(IConfiguration config, ILogger<BlobConversationState> logger)
    {
        _logger = logger;
        var connectionString = config["ConversationStorage:ConnectionString"]
            ?? throw new InvalidOperationException("ConversationStorage:ConnectionString no está configurado.");

        _container = new BlobContainerClient(connectionString, "conversaciones");
    }

    public Task<AgentContext> GetOrCreateAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.GetOrAdd(sessionId, id => new AgentContext
        {
            SessionId = id,
            Channel = "webchat"
        }));

    public async Task SaveAsync(AgentContext context, CancellationToken cancellationToken = default)
    {
        _sessions[context.SessionId] = context;

        try
        {
            await EnsureContainerAsync(cancellationToken);

            var blob = _container.GetBlobClient(BlobName(context.SessionId));
            await blob.UploadAsync(BinaryData.FromObjectAsJson(context), overwrite: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Conversaciones] No se pudo archivar la sesión {SessionId} en Blob Storage.",
                context.SessionId);
        }
    }

    private Task EnsureContainerAsync(CancellationToken cancellationToken) =>
        _initTask ??= _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

    private static string BlobName(string sessionId)
    {
        var clean = new string(sessionId.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        if (clean.Length > 80)
            clean = clean[..80];
        return $"sesion-{clean}.json";
    }
}
