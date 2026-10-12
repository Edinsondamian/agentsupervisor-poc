using Azure;
using Azure.AI.DocumentIntelligence;
using agentsupervisorpoc.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class DocumentIntelligenceReader : IDocumentOcrService
{
    private readonly Lazy<DocumentIntelligenceClient?> _client;
    private readonly string _modelId;
    private readonly ILogger<DocumentIntelligenceReader> _logger;

    public DocumentIntelligenceReader(IConfiguration config, ILogger<DocumentIntelligenceReader> logger)
    {
        _logger = logger;
        _modelId = config["DocumentIntelligence:ModelId"] ?? "prebuilt-read";

        _client = new Lazy<DocumentIntelligenceClient?>(() =>
        {
            var endpoint = config["DocumentIntelligence:Endpoint"];
            var key = config["DocumentIntelligence:Key"];

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
                return null;

            return new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key));
        });
    }

    public async Task<string?> ExtractTextAsync(
        Stream pdfContent,
        CancellationToken cancellationToken = default)
    {
        var client = _client.Value;
        if (client is null)
        {
            _logger.LogWarning(
                "[OCR] Document Intelligence no está configurado: no se leerán PDFs escaneados.");
            return null;
        }

        try
        {
            pdfContent.Position = 0;
            var bytes = BinaryData.FromStream(pdfContent);

            var operation = await client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _modelId,
                bytes,
                cancellationToken);

            var text = operation.Value?.Content;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[OCR] Falló el análisis del documento con Document Intelligence.");
            return null;
        }
    }
}
