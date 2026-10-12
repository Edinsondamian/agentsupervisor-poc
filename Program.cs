using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Infrastructure.Services;
using agentsupervisorpoc.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentSupervisorSolution(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration["AzureOpenAI:Endpoint"]))
{
    app.Logger.LogWarning(
        "AzureOpenAI:Endpoint/Key no están configurados. Establezcalos en appsettings.Development.json " +
        "o con: dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://...\" / " +
        "dotnet user-secrets set \"AzureOpenAI:Key\" \"...\". El chat responderá con error hasta configurarlos.");
}

var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("web.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapHealthChecks("/health");

app.MapPost("/api/chat", async (
    ChatRequest request,
    IConfiguration config,
    IAgentOrchestration orchestration,
    IConversationState state,
    IAttachmentStore attachments,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (string.Equals(request?.Event, "webchat/join", StringComparison.OrdinalIgnoreCase))
    {
        var joinSessionId = string.IsNullOrWhiteSpace(request!.SessionId)
            ? Guid.NewGuid().ToString()
            : request.SessionId!;

        var joinContext = await state.GetOrCreateAsync(joinSessionId);

        return Results.Ok(new ChatResponse(
            joinContext.SessionId, BuildWelcomeMessage(config), "WelcomePipeline"));
    }

    var hasAttachments = request?.AttachmentIds is { Count: > 0 };

    if (string.IsNullOrWhiteSpace(request?.Message) && !hasAttachments)
        return Results.BadRequest(new { error = "Campo 'message' requerido." });

    if (string.IsNullOrWhiteSpace(config["AzureOpenAI:Endpoint"]))
        return Results.Json(
            new { error = "Azure OpenAI no está configurado. Complete AzureOpenAI:Endpoint y AzureOpenAI:Key en appsettings.Development.json o en user-secrets, y reinicie la aplicación." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var sessionId = string.IsNullOrWhiteSpace(request!.SessionId)
        ? Guid.NewGuid().ToString()
        : request.SessionId!;

    var context = await state.GetOrCreateAsync(sessionId, ct);
    var resolvedAttachments = attachments.Resolve(request.AttachmentIds);

    if (resolvedAttachments.Count > AttachmentStore.MaxFilesPerMessage)
        return Results.BadRequest(new
        {
            error = $"Máximo {AttachmentStore.MaxFilesPerMessage} documentos por mensaje."
        });

    if (resolvedAttachments.Select(a => a.Kind).Distinct().Count() > 1)
        return Results.BadRequest(new
        {
            error = "Solo puedes enviar PDF o imágenes, no ambos."
        });

    try
    {
        var response = await orchestration.ProcessAsync(
            request.Message ?? string.Empty, context, resolvedAttachments, ct);

        return Results.Ok(new ChatResponse(
            context.SessionId, response.Content, response.AgentName, response.TokensUsed));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error procesando /api/chat. SessionId={SessionId}", context.SessionId);
        return Results.Json(
            new { error = config["MensajesError:ErrorGeneral"] ?? "Error procesando la consulta." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/attachments", async (
    HttpRequest http,
    IConversationState state,
    IAttachmentStore store,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "Se requiere multipart/form-data con el campo 'file'." });

    IFormFile? file;
    try
    {
        var form = await http.ReadFormAsync(ct);
        file = form.Files.FirstOrDefault(f => f.Name == "file")
               ?? form.Files.FirstOrDefault();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Formulario de adjunto inválido.");
        return Results.BadRequest(new { error = "Formulario inválido." });
    }

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Campo 'file' requerido (PDF o imagen JPG/PNG)." });

    var isPdfName = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    var isImageName = file.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || file.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || file.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(file.ContentType, "image/png", StringComparison.OrdinalIgnoreCase)
        || string.Equals(file.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

    if (!isPdfName && !isImageName)
        return Results.BadRequest(new { error = "Solo se permiten archivos PDF o imágenes (JPG/PNG)." });

    if (file.Length > AttachmentStore.MaxAttachmentBytes)
        return Results.BadRequest(new
        {
            error = $"El archivo supera el límite de {AttachmentStore.MaxAttachmentBytes / (1024 * 1024)} MB."
        });

    var sessionId = http.Form["sessionId"].ToString();
    if (string.IsNullOrWhiteSpace(sessionId))
        sessionId = Guid.NewGuid().ToString();

    var context = await state.GetOrCreateAsync(sessionId, ct);

    try
    {
        using var stream = file.OpenReadStream();
        var info = await store.SaveAsync(context.SessionId, file.FileName, stream, ct);

        return Results.Ok(new AttachmentResponse(
            context.SessionId, info.FileId, info.Name, info.SizeBytes, info.ExtractedChars));
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error procesando adjunto {FileName}", file.FileName);
        return Results.Json(
            new { error = "No se pudo procesar el archivo PDF." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static string BuildWelcomeMessage(IConfiguration config)
{
    var texto = config["MensajesBienvenida:Saludo"]
        ?? "Soy el Asistente de Evaluación Crediticia. ¿Empezamos?";

    var peru = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
    var ahora = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, peru);

    var saludo = ahora.Hour switch
    {
        >= 5 and < 12 => "Buenos días",
        >= 12 and < 19 => "Buenas tardes",
        _ => "Buenas noches"
    };

    return $"¡Hola! 👋 {saludo}. {texto}";
}
