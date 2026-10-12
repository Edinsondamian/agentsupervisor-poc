using System.Collections.Concurrent;
using System.Text;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Models;
using Microsoft.Extensions.Logging;

namespace agentsupervisorpoc.Infrastructure.Services;

public sealed class AttachmentStore : IAttachmentStore
{
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;
    public const int MaxExtractedChars = 15000;
    public const int MaxFilesPerMessage = 10;

    private static readonly byte[] PdfHeader = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegHeader = { 0xFF, 0xD8, 0xFF };

    private readonly ConcurrentDictionary<string, AttachmentInfo> _attachments = new();
    private readonly IDocumentOcrService _documentIntelligence;
    private readonly ILogger<AttachmentStore> _logger;

    public AttachmentStore(IDocumentOcrService documentIntelligence, ILogger<AttachmentStore> logger)
    {
        _documentIntelligence = documentIntelligence;
        _logger = logger;
    }

    public async Task<AttachmentInfo> SaveAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        if (buffer.Length == 0)
            throw new InvalidDataException("El archivo está vacío.");

        if (buffer.Length > MaxAttachmentBytes)
            throw new InvalidDataException(
                $"El archivo supera el límite de {MaxAttachmentBytes / (1024 * 1024)} MB.");

        var kind = DetectKind(buffer);
        var text = await ExtractTextAsync(buffer, cancellationToken);

        var info = new AttachmentInfo
        {
            SessionId = sessionId,
            Name = Path.GetFileName(fileName),
            SizeBytes = buffer.Length,
            ExtractedText = text,
            Kind = kind
        };
        _attachments[info.FileId] = info;

        _logger.LogInformation(
            "[Adjuntos] Documento guardado: '{Name}' ({SizeKB} KB, tipo={Kind}, {Chars} chars, fuente={Source}). SessionId={SessionId}, FileId={FileId}",
            info.Name, info.SizeBytes / 1024, kind, info.ExtractedChars,
            string.IsNullOrWhiteSpace(text) ? "sin-texto" : "Document Intelligence",
            sessionId, info.FileId);

        return info;
    }

    public IReadOnlyList<AttachmentInfo> Resolve(IEnumerable<string?>? fileIds)
    {
        if (fileIds is null)
            return [];

        var resolved = new List<AttachmentInfo>();
        foreach (var id in fileIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && _attachments.TryGetValue(id, out var info))
                resolved.Add(info);
        }
        return resolved;
    }

    public static string? ValidateForMessage(IReadOnlyList<AttachmentInfo> attachments)
    {
        if (attachments.Count > MaxFilesPerMessage)
            return $"Máximo {MaxFilesPerMessage} documentos por mensaje.";

        if (attachments.Select(a => a.Kind).Distinct().Count() > 1)
            return "Solo puedes enviar PDF o imágenes, no ambos.";

        return null;
    }

    private async Task<string?> ExtractTextAsync(
        MemoryStream document,
        CancellationToken cancellationToken)
    {
        var text = await _documentIntelligence.ExtractTextAsync(document, cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Length > MaxExtractedChars)
            text = text[..MaxExtractedChars];

        return text;
    }

    private static string DetectKind(MemoryStream buffer)
    {
        var header = new byte[8];
        buffer.ReadExactly(header);
        buffer.Position = 0;

        if (header.AsSpan(0, 5).SequenceEqual(PdfHeader))
            return "pdf";

        if (header.AsSpan(0, 8).SequenceEqual(PngHeader) || header.AsSpan(0, 3).SequenceEqual(JpegHeader))
            return "imagen";

        throw new InvalidDataException("El archivo no es un PDF ni una imagen (JPG/PNG) válida.");
    }
}
