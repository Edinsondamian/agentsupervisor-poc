using agentsupervisorpoc.Application.Agents;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Application.Services;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Infrastructure.Services;
using agentsupervisorpoc.Models;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Hosting.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentSupervisorSolution(builder.Configuration);
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient();

builder.Services.AddDefaultMsalAuth(builder.Configuration);
builder.AddAgent<CreditEvaluationBot>();

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
defaultFiles.DefaultFileNames.Add("directline.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapGet("/local", () => Results.Redirect("/local.html"));

app.MapAgentEndpoints(requireAuth: false);

app.MapHealthChecks("/health");

app.MapGet("/api/directline/token", async (
    IConfiguration config,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var secret = config["Directline:Root"];
    if (string.IsNullOrWhiteSpace(secret))
        return Results.Json(
            new { error = "DirectLine no está configurado. Registre el bot en Azure Bot Service (endpoint /api/messages), habilite el canal DirectLine y configure el secreto: dotnet user-secrets set \"Directline:Root\" \"<secreto>\"." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var baseUrl = (config["DirectlineToken:Url"] ?? "https://directline.botframework.com").TrimEnd('/');
    var userId = "dl_" + Guid.NewGuid().ToString("N");

    try
    {
        var http = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/directline/tokens/generate")
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { user = new { id = userId } }),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Token DirectLine rechazado: {Status} {Body}",
                (int)response.StatusCode, body);
            return Results.Json(
                new { error = "No se pudo generar el token de DirectLine (secreto inválido o canal no habilitado)." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        var token = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("token").GetString();
        return Results.Ok(new { token, userId });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error generando token de DirectLine.");
        return Results.Json(
            new { error = "Error generando el token de DirectLine." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

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
            joinContext.SessionId, WelcomeService.Build(config), "WelcomePipeline"));
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

    var validationError = AttachmentStore.ValidateForMessage(resolvedAttachments);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

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
