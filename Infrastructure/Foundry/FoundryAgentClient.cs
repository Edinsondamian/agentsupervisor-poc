using System.Collections.Concurrent;
using Azure.AI.Extensions.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

#pragma warning disable OPENAI001

namespace agentsupervisorpoc.Infrastructure.Foundry;

public sealed class FoundryAgentClient
{
    private readonly Lazy<ProjectOpenAIClient> _projectOpenAI;
    private readonly Lazy<HttpClient> _httpClient;
    private readonly ConcurrentDictionary<string, ProjectResponsesClient> _agentClients = new();
    private readonly ILogger<FoundryAgentClient> _logger;
    private readonly IConfiguration _config;

    public FoundryAgentClient(IConfiguration config, ILogger<FoundryAgentClient> logger)
    {
        _config = config;
        _logger = logger;

        _projectOpenAI = new Lazy<ProjectOpenAIClient>(() =>
        {
            var endpoint = _config["AzureOpenAI:Endpoint"]
                ?? throw new InvalidOperationException(
                    "AzureOpenAI:Endpoint no está configurado (endpoint del proyecto de Azure AI Foundry).");

            var apiKey = _config["AzureOpenAI:Key"] ?? string.Empty;

            return new ProjectOpenAIClient(
                new Uri(endpoint),
                new ApiKeyTokenProvider(apiKey),
                new ProjectOpenAIClientOptions());
        });

        _httpClient = new Lazy<HttpClient>(() =>
        {
            var apiKey = _config["AzureOpenAI:Key"];
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "AzureOpenAI:Key es requerido para invocar a los agentes de Azure AI Foundry.");

            return new HttpClient(new ApiKeyHandler(apiKey));
        });
    }

    public async Task<string> RunAsync(string agentName, string message, CancellationToken cancellationToken = default)
    {
        var client = _agentClients.GetOrAdd(agentName, name =>
        {
            var options = new ProjectOpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(_httpClient.Value)
            };

            return _projectOpenAI.Value.GetProjectResponsesClientForAgentEndpoint(name, options: options);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.CreateResponseAsync(message, cancellationToken: cancellationToken);
        var result = response.Value;
        sw.Stop();

        var output = result.GetOutputText();
        _logger.LogInformation("[Foundry] Agente '{Agent}' respondió en {Elapsed} ms ({Chars} chars, RESULTADO={Resultado}).",
            agentName, sw.ElapsedMilliseconds, output?.Length ?? 0, TryGetResultado(output) ?? "n/d");

        return output ?? string.Empty;
    }

    private static string? TryGetResultado(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var json = output.Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
                json = json[start..(end + 1)];
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("RESULTADO", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (output.Contains("No Encontrado", StringComparison.OrdinalIgnoreCase)
            || output.Contains("NO_ENCONTRADO", StringComparison.OrdinalIgnoreCase))
            return "NO_ENCONTRADO";

        if (output.Contains("Con Discrepancias", StringComparison.OrdinalIgnoreCase))
            return "OBSERVADO";

        if (output.Contains("Válido", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Aprobado", StringComparison.OrdinalIgnoreCase))
            return "APROBADO";

        return null;
    }

    private sealed class ApiKeyHandler : DelegatingHandler
    {
        private readonly string _apiKey;

        public ApiKeyHandler(string apiKey) : base(new HttpClientHandler()) => _apiKey = apiKey;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("api-key", _apiKey);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ApiKeyTokenProvider : AuthenticationTokenProvider
    {
        private readonly string _apiKey;
        private static readonly DateTimeOffset ExpiresOn = DateTimeOffset.UtcNow.AddYears(1);

        public ApiKeyTokenProvider(string apiKey) => _apiKey = apiKey;

        public override GetTokenOptions CreateTokenOptions(IReadOnlyDictionary<string, object> properties)
            => new(properties);

        public override AuthenticationToken GetToken(GetTokenOptions options, CancellationToken cancellationToken = default)
            => new(_apiKey, "Bearer", ExpiresOn);

        public override ValueTask<AuthenticationToken> GetTokenAsync(GetTokenOptions options, CancellationToken cancellationToken = default)
            => new(new AuthenticationToken(_apiKey, "Bearer", ExpiresOn));
    }
}
