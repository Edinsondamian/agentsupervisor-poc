using agentsupervisorpoc.Application.Interfaces;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class LocalPromptProvider : IPromptProvider
{
    private readonly ILogger<LocalPromptProvider> _logger;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<Task> _loadOnce;

    public LocalPromptProvider(ILogger<LocalPromptProvider> logger)
    {
        _logger = logger;
        _loadOnce = new Lazy<Task>(LoadFromLocalAsync);
    }

    public string Get(string agentName)
    {
        if (!_loadOnce.Value.IsCompleted)
            _loadOnce.Value.GetAwaiter().GetResult();

        return _cache.TryGetValue(agentName, out var text) ? text : string.Empty;
    }

    public Task WarmupAsync(CancellationToken cancellationToken = default) => _loadOnce.Value;

    private async Task LoadFromLocalAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "AgentPrompts");

        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"No existe la carpeta de prompts: '{folder}'.");

        var loaded = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var text = await File.ReadAllTextAsync(file);
            _cache[name] = text;
            loaded.Add(name);

            _logger.LogInformation(
                "[Prompts] Cargado desde ARCHIVO LOCAL: '{File}' ({Length} chars).",
                file, text.Length);
        }

        if (loaded.Count == 0)
            throw new InvalidOperationException($"La carpeta '{folder}' no contiene prompts .txt.");

        _logger.LogInformation(
            "[Prompts] FUENTE = LOCAL (AgentPrompts). cantidad={Count}, archivos=[{Files}].",
            loaded.Count, string.Join(", ", loaded));
    }
}
