using Azure.Core;
using agentsupervisorpoc.Application.Agents;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Application.Services;
using agentsupervisorpoc.Infrastructure.Foundry;
using agentsupervisorpoc.Infrastructure.Plugins;
using agentsupervisorpoc.Infrastructure.Services;

namespace agentsupervisorpoc.Infrastructure.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentSupervisorSolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<TokenCredential>(_ => new Azure.Identity.DefaultAzureCredential());

        services.AddSingleton<IPromptProvider, LocalPromptProvider>();

        services.AddSingleton<AgentFactory>();

        services.AddSingleton<FoundryAgentClient>();
        services.AddSingleton<FoundryAgentsPlugin>();

        services.AddSingleton<OrchestratorAgent>();

        if (!string.IsNullOrWhiteSpace(configuration["ConversationStorage:ConnectionString"]))
        {
            services.AddSingleton<IConversationState, BlobConversationState>();
        }
        else
        {
            services.AddSingleton<IConversationState, InMemoryConversationState>();
        }

        services.AddSingleton<IDocumentOcrService, DocumentIntelligenceReader>();
        services.AddSingleton<IAttachmentStore, AttachmentStore>();
        services.AddSingleton<IFoundryAgentRegistry, FoundryAgentRegistry>();
        services.AddSingleton<IAgentOrchestration, AgentOrchestrationService>();

        services.AddSingleton<IWhatsAppMediaClient, WhatsAppMediaClient>();
        services.AddSingleton<IWhatsAppSender, WhatsAppSender>();

        return services;
    }
}
