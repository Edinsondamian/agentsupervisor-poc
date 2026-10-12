using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using agentsupervisorpoc.Application.Interfaces;

namespace agentsupervisorpoc.Infrastructure.Configuration;

public sealed class AgentFactory
{
    private const string CommonGuidelinesName = "CommonGuidelines";

    private readonly Lazy<IChatClient> _chatClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPromptProvider _promptProvider;
    private readonly IConfiguration _config;

    public AgentFactory(
        IConfiguration config,
        TokenCredential credential,
        ILoggerFactory loggerFactory,
        IPromptProvider promptProvider)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _promptProvider = promptProvider;

        _chatClient = new Lazy<IChatClient>(() =>
        {
            var endpoint = config["AzureOpenAI:Endpoint"]
                ?? throw new InvalidOperationException(
                    "AzureOpenAI:Endpoint no está configurado. Establezca 'AzureOpenAI:Endpoint' y 'AzureOpenAI:Key' en appsettings.Development.json o con user-secrets.");

            var model = config["AzureOpenAI:DeploymentName"] ?? "gpt-5";
            var apiKey = config["AzureOpenAI:Key"];

            return BuildChatClient(endpoint, model, apiKey, credential);
        });
    }

    private static IChatClient BuildChatClient(string endpoint, string model, string? apiKey, TokenCredential credential)
    {
        if (endpoint.Contains("services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "AzureOpenAI:Key es requerido para el endpoint de proyecto de Azure AI Foundry.");

            var v1 = new Uri(endpoint.TrimEnd('/') + "/openai/v1");

            var client = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = v1 });

            return client.GetChatClient(model).AsIChatClient();
        }

        AzureOpenAIClient azureClient = !string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
            : new AzureOpenAIClient(new Uri(endpoint), credential);

        return azureClient.GetChatClient(model).AsIChatClient();
    }

    public IChatClient ChatClient => _chatClient.Value;

    public ChatClientAgent BuildAgent(string agentName, IEnumerable<AITool>? tools = null)
        => new ChatClientAgent(
            ChatClient,
            new ChatClientAgentOptions
            {
                Name = agentName,
                Description = agentName,
                ChatOptions = new ChatOptions
                {
                    Instructions = BuildInstructions(agentName),
                    Temperature = ResolveTemperature(agentName),
                    MaxOutputTokens = _config.GetValue<int?>("AgentSettings:MaxResponseTokens"),
                    Tools = tools?.ToList()
                }
            },
            _loggerFactory,
            services: null);

    private float? ResolveTemperature(string agentName)
    {
        var specificKey = agentName switch
        {
            "OrchestratorAgent" => "AgentSettings:OrchestratorTemperature",
            _ => null
        };

        return (specificKey is null ? null : _config.GetValue<float?>(specificKey))
            ?? _config.GetValue<float?>("AgentSettings:DefaultTemperature");
    }

    private string BuildInstructions(string agentName)
    {
        var common = _promptProvider.Get(CommonGuidelinesName);
        var specific = _promptProvider.Get(agentName);
        return string.IsNullOrEmpty(common) ? specific : $"{common}\n\n{specific}";
    }

    public static string LoadInstructions(string agentName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AgentPrompts", $"{agentName}.txt");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
