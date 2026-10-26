using System.Net.Http.Headers;
using System.Text.Json;
using agentsupervisorpoc.Application.Interfaces;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class WhatsAppMediaClient : IWhatsAppMediaClient
{
    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string _graphUrl;
    private readonly ILogger<WhatsAppMediaClient> _logger;

    public WhatsAppMediaClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<WhatsAppMediaClient> logger)
    {
        _http = httpClientFactory.CreateClient("whatsapp");
        _accessToken = configuration["WhatsApp:AccessToken"] ?? string.Empty;
        _graphUrl = (configuration["WhatsApp:GraphUrl"] ?? "https://graph.facebook.com").TrimEnd('/');
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_accessToken))
            logger.LogWarning(
                "WhatsApp:AccessToken no está configurado. No se podrán descargar documentos/imágenes de WhatsApp.");
    }

    public async Task<(Stream Content, string FileName, string MimeType)> DownloadAsync(
        string mediaId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new InvalidOperationException(
                "WhatsApp:AccessToken no está configurado. Complételo en user-secrets o appsettings.");

        using var metaRequest = new HttpRequestMessage(HttpMethod.Get, $"{_graphUrl}/{mediaId}");
        metaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var metaResponse = await _http.SendAsync(metaRequest, cancellationToken);
        var metaBody = await metaResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!metaResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Meta rechazó la descarga de media {MediaId}: {Status} {Body}",
                mediaId, (int)metaResponse.StatusCode, metaBody);
            throw new InvalidOperationException("No se pudo obtener la información del documento en WhatsApp.");
        }

        using var metaDoc = JsonDocument.Parse(metaBody);
        var url = metaDoc.RootElement.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
        var mimeType = metaDoc.RootElement.TryGetProperty("mime_type", out var mimeEl) ? mimeEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("El media en WhatsApp no tiene una URL de descarga disponible.");

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, url);
        downloadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var downloadResponse = await _http.SendAsync(downloadRequest, cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Falló la descarga de media {MediaId}: {Status}", mediaId, (int)downloadResponse.StatusCode);
            throw new InvalidOperationException("No se pudo descargar el documento desde WhatsApp.");
        }

        var stream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var fileName = metaDoc.RootElement.TryGetProperty("filename", out var nameEl) && !string.IsNullOrWhiteSpace(nameEl.GetString())
            ? nameEl.GetString()!
            : DeriveFileName(mimeType);

        return (buffer, fileName, mimeType ?? "application/pdf");
    }

    private static string DeriveFileName(string? mimeType) => mimeType switch
    {
        var m when m is not null && m.Contains("png", StringComparison.OrdinalIgnoreCase) => "imagen.png",
        var m when m is not null && (m.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || m.Contains("jpg", StringComparison.OrdinalIgnoreCase)) => "imagen.jpg",
        _ => "documento.pdf"
    };
}
