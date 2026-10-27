using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using agentsupervisorpoc.Application.Agents;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Infrastructure.Foundry;
using agentsupervisorpoc.Models;

namespace agentsupervisorpoc.Application.Services;

public sealed class AgentOrchestrationService : IAgentOrchestration
{
    private const int MaxInputLength = 4000;
    private const int MaxThreadMessages = 20;

    private readonly OrchestratorAgent _orchestrator;
    private readonly IConversationState _conversationState;
    private readonly IFoundryAgentRegistry _foundryAgents;
    private readonly FoundryAgentClient _foundryAgentClient;
    private readonly ILogger<AgentOrchestrationService> _logger;
    private readonly string _errorGeneral;

    public AgentOrchestrationService(
        OrchestratorAgent orchestrator,
        IConversationState conversationState,
        IFoundryAgentRegistry foundryAgents,
        FoundryAgentClient foundryAgentClient,
        IConfiguration configuration,
        ILogger<AgentOrchestrationService> logger)
    {
        _orchestrator = orchestrator;
        _conversationState = conversationState;
        _foundryAgents = foundryAgents;
        _foundryAgentClient = foundryAgentClient;
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

            userInput = "(El usuario envió este mensaje únicamente con documentos adjuntos, sin texto adicional.)";
        }

        if (userInput.Length > MaxInputLength)
            return new AgentResponse(
                "Tu mensaje es demasiado largo. Por favor, resúmelo en menos de 4000 caracteres.",
                "ValidationPipeline");

        try
        {
            AgentResponse? response = null;

            if (hasAttachments)
            {
                var content = BuildDocumentContent(attachments!);
                context.LastDocumentContent = content;

                var creditOutput = await TryRunCreditValidationAsync(
                    BuildCreditValidationPayload(attachments!),
                    cancellationToken);

                AgentResponse? docOutput = null;
                if (creditOutput is null || IsCreditResultFavorable(creditOutput))
                {
                    var content2 = creditOutput is null ? content : $"{content}\n\n[Resultado de validación crediticia (lista negra)]: {creditOutput}";
                    docOutput = await TryRunDocumentEvaluationAsync(content2, cancellationToken);
                }

                response = BuildCombinedResponse(creditOutput, docOutput);
            }

            response ??= await _orchestrator.ExecuteAsync(
                BuildAgentInput(userInput, attachments, context), context, cancellationToken);

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

    private static string BuildDocumentContent(IReadOnlyList<AttachmentInfo> attachments)
    {
        var sb = new StringBuilder();
        foreach (var a in attachments)
        {
            sb.AppendLine($"Documento: {a.Name}");
            sb.AppendLine(a.ExtractedText ?? "(sin texto extraído)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildCreditValidationPayload(IReadOnlyList<AttachmentInfo> attachments)
    {
        var text = string.Join("\n", attachments.Select(a => a.ExtractedText ?? string.Empty));

        var documentNumber = Regex.Match(text, @"\b(\d{8})\b").Groups[1].Value;
        var monto = Regex.Match(text, @"Monto\s+solicitado[\s\S]{0,200}?S/\s*([\d,]+(?:\.\d+)?)").Groups[1].Value.Replace(",", string.Empty);
        var plazo = Regex.Match(text, @"Plazo\s+solicitado[\s\S]{0,80}?(\d{1,3})\s*meses").Groups[1].Value;
        var actividad = Regex.Match(text, @"Cargo o actividad econ[oó]mica[\s\S]{0,200}?\d{4} - ([^\n]+)").Groups[1].Value.Trim();
        var ingreso = Regex.Match(text, @"Ingreso mensual bruto \(ventas\)[\s\S]{0,80}?S/\s*([\d,]+(?:\.\d+)?)").Groups[1].Value.Replace(",", string.Empty);

        var payload = new
        {
            tipoDocumento = documentNumber.Length == 8 ? "DNI" : "NO_ENCONTRADO",
            numeroDocumento = documentNumber,
            montoSolicitado = decimal.TryParse(monto, out var m) ? m : 0m,
            moneda = "PEN",
            plazoMeses = int.TryParse(plazo, out var p) ? p : 0,
            actividadNegocio = string.IsNullOrWhiteSpace(actividad) ? "NO_ENCONTRADO" : actividad.ToUpperInvariant(),
            ingresoMensual = decimal.TryParse(ingreso, out var i) ? i : 0m
        };

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private async Task<string?> TryRunCreditValidationAsync(
        string documentContent,
        CancellationToken cancellationToken)
    {
        var agent = _foundryAgents.Get(FoundryAgentRegistry.CreditValidationAgent);
        if (agent is null || !agent.IsConfigured)
        {
            _logger.LogWarning("[Crediticia] Agente '{Agent}' no configurado.", agent?.Name ?? FoundryAgentRegistry.CreditValidationAgent);
            return null;
        }

        try
        {
            var output = await _foundryAgentClient.RunAsync(agent.AgentId, documentContent, cancellationToken);

            _logger.LogInformation("[Crediticia] Respuesta del agente '{Agent}':\n{Output}",
                agent.AgentId, output);

            return string.IsNullOrWhiteSpace(output) ? null : RemoveIdValidacion(output, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Crediticia] Falló la validación crediticia; se continúa con la evaluación documental.");
            return null;
        }
    }

    private static string RemoveIdValidacion(string output, ILogger logger)
    {
        var trimmed = output.Trim();
        if (trimmed.StartsWith("```"))
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
                trimmed = trimmed[start..(end + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject().Where(p => !p.NameEquals("idValidacion")))
                    prop.WriteTo(writer);
                writer.WriteEndObject();
            }

            var cleaned = Encoding.UTF8.GetString(ms.ToArray());
            logger.LogInformation("[Crediticia] Respuesta del agente para el usuario:\n{Output}", cleaned);
            return cleaned;
        }
        catch (System.Text.Json.JsonException)
        {
            return output;
        }
    }

    private static bool IsCreditResultFavorable(string? creditOutput)
    {
        if (string.IsNullOrWhiteSpace(creditOutput))
            return false;

        var trimmed = creditOutput.Trim();

        if (trimmed.Contains("no está habilitado", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("No Encontrado", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("NO_ENCONTRADO", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("inhabilitado", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("resultado", out var result)
                && result.ValueKind == JsonValueKind.String)
            {
                var value = result.GetString()!;
                return !value.Contains("DENEGADO", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains("RECHAZADO", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains("OBSERVADO", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
        }

        return true;
    }

    private static AgentResponse BuildCombinedResponse(
        string? creditOutput,
        AgentResponse? docOutput)
    {
        if (docOutput is null && creditOutput is null)
            return new AgentResponse(
                "No se pudo completar el procesamiento de los documentos en este momento. Por favor, inténtalo nuevamente.", "ValidationPipeline");

        var sb = new StringBuilder();

        if (creditOutput is not null)
        {
            sb.AppendLine("🏦 VALIDACIÓN CREDITICIA");
            sb.Append(FormatCreditForUser(creditOutput));
        }

        if (docOutput is not null)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("📋 EVALUACIÓN DOCUMENTAL");
            sb.AppendLine(docOutput.Content);
        }

        return new AgentResponse(sb.ToString().TrimEnd(), "ValidationPipeline");
    }

    private static string FormatCreditForUser(string creditOutput)
    {
        var trimmed = creditOutput.Trim();

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            string resultado = root.TryGetProperty("resultado", out var r) ? r.GetString() ?? "" : "";
            var aprobado = resultado.Equals("APROBADO", StringComparison.OrdinalIgnoreCase);
            string score = root.TryGetProperty("scoreCrediticio", out var s) ? s.ValueKind == JsonValueKind.Number ? s.GetInt32().ToString() : "" : "";
            string riesgo = root.TryGetProperty("nivelRiesgo", out var n) ? n.GetString() ?? "" : "";
            string monto = root.TryGetProperty("montoAprobado", out var mo) && mo.ValueKind == JsonValueKind.Number ? $"S/{mo.GetDecimal():N2}" : "";
            string cuota = root.TryGetProperty("cuotaEstimada", out var c) && c.ValueKind == JsonValueKind.Number ? $"S/{c.GetDecimal():N2}" : "";
            var observaciones = root.TryGetProperty("observaciones", out var obs) && obs.ValueKind == JsonValueKind.Array
                ? obs.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                : new List<string?>();
            string fecha = root.TryGetProperty("fechaEvaluacion", out var f) && DateTime.TryParse(f.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt.AddHours(-5).ToString("dd/MM/yyyy hh:mm tt", new CultureInfo("es-PE"))
                : "";

            var sb = new StringBuilder();
            sb.AppendLine(aprobado
                ? $"✅ Crédito {resultado}: {monto}"
                : $"❌ Crédito {resultado}");
            if (!string.IsNullOrEmpty(score) || !string.IsNullOrEmpty(riesgo))
                sb.AppendLine($"• Score crediticio: {score}   • Nivel de riesgo: {riesgo}".TrimEnd());
            if (!string.IsNullOrEmpty(cuota))
                sb.AppendLine($"• Cuota estimada: {cuota}");
            sb.AppendLine(observaciones.Count > 0
                ? $"• Observaciones: {string.Join("; ", observaciones!)}"
                : "• Observaciones: ninguna.");
            if (!string.IsNullOrEmpty(fecha))
                sb.AppendLine($"• Evaluado: {fecha}");

            return sb.ToString();
        }
        catch (JsonException)
        {
            return trimmed + Environment.NewLine;
        }
    }

    private async Task<AgentResponse?> TryRunDocumentEvaluationAsync(
        string documentContent,
        CancellationToken cancellationToken)
    {
        var agent = _foundryAgents.Get(FoundryAgentRegistry.DocumentationEvaluationAgent);
        if (agent is null || !agent.IsConfigured)
            return null;

        try
        {
            _logger.LogInformation(
                "[Documentacion] Texto extraído por Document Intelligence que se envía al agente '{Agent}' ({Chars} chars):\n{Content}",
                agent.AgentId, documentContent.Length, documentContent);

            var output = await _foundryAgentClient.RunAsync(agent.AgentId, documentContent, cancellationToken);

            _logger.LogInformation(
                "[Documentacion] Respuesta del agente '{Agent}':\n{Output}",
                agent.AgentId, output);

            return string.IsNullOrWhiteSpace(output)
                ? null
                : new AgentResponse(output, agent.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Documentacion] Falló la evaluación documental directa; se responde con el orquestador.");
            return null;
        }
    }

    private string BuildAgentInput(
        string userInput,
        IReadOnlyList<AttachmentInfo>? attachments,
        AgentContext context)
    {
        if (attachments is { Count: > 0 })
        {
            var sb = new StringBuilder(userInput.TrimEnd());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"[CONTEXTO: el usuario adjuntó {attachments.Count} documento(s) en este mensaje]");
            foreach (var a in attachments)
            {
                sb.AppendLine($"- {a.Name} ({a.SizeBytes / 1024.0:F0} KB)");
                if (!string.IsNullOrWhiteSpace(a.ExtractedText))
                {
                    sb.AppendLine("Contenido extraído del documento (puede estar truncado):");
                    sb.AppendLine("\"\"\"");
                    sb.AppendLine(a.ExtractedText);
                    sb.AppendLine("\"\"\"");
                }
                else
                {
                    sb.AppendLine("(No se pudo extraer texto del documento: puede ser un documento escaneado o sin texto).");
                }
            }
            sb.Append("Usa el contenido de los adjuntos, junto con los datos de la conversación, para atender el mensaje del usuario.");
            return sb.ToString();
        }

        var previous = FindLastDocumentEvaluation(context);
        if (previous is null && string.IsNullOrWhiteSpace(context.LastDocumentContent))
            return userInput;

        var contextSb = new StringBuilder();

        if (previous is not null)
        {
            contextSb.AppendLine("[Evaluación documental previa del agente de Foundry]");
            contextSb.AppendLine(previous.Content);
            contextSb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(context.LastDocumentContent))
        {
            contextSb.AppendLine("[Contenido del último documento evaluado]");
            contextSb.AppendLine(context.LastDocumentContent);
            contextSb.AppendLine();
        }

        contextSb.Append(userInput);
        return contextSb.ToString();
    }

    private DomainMessage? FindLastDocumentEvaluation(AgentContext context)
    {
        var agent = _foundryAgents.Get(FoundryAgentRegistry.DocumentationEvaluationAgent);
        if (agent is null)
            return null;

        for (var i = context.ConversationThread.Count - 1; i >= 0; i--)
        {
            var message = context.ConversationThread[i];
            if (message.Role == "assistant"
                && string.Equals(message.AgentName, agent.AgentId, StringComparison.OrdinalIgnoreCase))
                return message;
        }

        return null;
    }

    private static string DescribeForThread(string userInput, IReadOnlyList<AttachmentInfo>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return userInput;

        var names = string.Join(", ", attachments.Select(a => a.Name));
        return $"{userInput} [Documento adjunto: {names}]";
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
