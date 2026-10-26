using System.Text;
using System.Text.Json;
using agentsupervisorpoc.Application.Interfaces;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class WhatsAppSender : IWhatsAppSender
{
    private readonly HttpClient _http;
    private readonly string? _url;
    private readonly ILogger<WhatsAppSender> _logger;

    public WhatsAppSender(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<WhatsAppSender> logger)
    {
        _http = httpClientFactory.CreateClient("whatsapp");
        _url = configuration["WhatsAppSendMessage:Url"];
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_url))
            logger.LogWarning(
                "WhatsAppSendMessage:Url no está configurado. No se podrán enviar respuestas por WhatsApp. " +
                "Configure la URL del microservicio de envío (ej. https://wa-sendmessage-.../whatsapp/envia/messages).");
    }

    public async Task<bool> SendTextAsync(
        string number, string? name, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_url))
        {
            _logger.LogWarning("No hay URL configurada para sendmessage; se descarta la respuesta para {Number}.", number);
            return false;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Type = "text",
                Number = number,
                Name = name,
                TextMessage = new { text }
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "ws-sendmessage rechazó el envío a {Number}: {Status} {Body}",
                    number, (int)response.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando mensaje a {Number} vía ws-sendmessage.", number);
            return false;
        }
    }
}
