using System.Collections.Concurrent;
using System.Text;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Models;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace agentsupervisorpoc.Infrastructure.Services;

public enum TextExtractionSource
{
    None,
    Digital,
    Ocr
}

public sealed class AttachmentStore : IAttachmentStore
{
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;
    public const int MaxExtractedChars = 15000;
    public const int MinDigitalChars = 30;
    public const int MaxFilesPerMessage = 10;

    private static readonly byte[] PdfHeader = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegHeader = { 0xFF, 0xD8, 0xFF };

    private readonly ConcurrentDictionary<string, AttachmentInfo> _attachments = new();
    private readonly IDocumentOcrService _ocr;
    private readonly ILogger<AttachmentStore> _logger;

    public AttachmentStore(IDocumentOcrService ocr, ILogger<AttachmentStore> logger)
    {
        _ocr = ocr;
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

        var (text, source) = kind == "pdf"
            ? await ExtractPdfTextAsync(buffer, cancellationToken)
            : await ExtractImageTextAsync(buffer, cancellationToken);

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
            info.Name, info.SizeBytes / 1024, kind, info.ExtractedChars, source, sessionId, info.FileId);

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

    private async Task<(string? Text, TextExtractionSource Source)> ExtractPdfTextAsync(
        MemoryStream pdf,
        CancellationToken cancellationToken)
    {
        var digital = TryExtractDigitalText(pdf);

        if (!string.IsNullOrWhiteSpace(digital) && digital.Trim().Length >= MinDigitalChars)
            return (digital, TextExtractionSource.Digital);

        var ocr = await _ocr.ExtractTextAsync(pdf, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ocr))
        {
            if (ocr.Length > MaxExtractedChars)
                ocr = ocr[..MaxExtractedChars];

            return (ocr, TextExtractionSource.Ocr);
        }

        if (!string.IsNullOrEmpty(digital))
            return (digital, TextExtractionSource.Digital);

        return (null, TextExtractionSource.None);
    }

    private async Task<(string? Text, TextExtractionSource Source)> ExtractImageTextAsync(
        MemoryStream image,
        CancellationToken cancellationToken)
    {
        var ocr = await _ocr.ExtractTextAsync(image, cancellationToken);

        if (string.IsNullOrWhiteSpace(ocr))
            return (null, TextExtractionSource.None);

        if (ocr.Length > MaxExtractedChars)
            ocr = ocr[..MaxExtractedChars];

        return (ocr, TextExtractionSource.Ocr);
    }

    private string? TryExtractDigitalText(MemoryStream pdf)
    {
        try
        {
            var sb = new StringBuilder();
            pdf.Position = 0;
            using var document = PdfDocument.Open(pdf);
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(string.Join(" ", page.GetWords()));
                if (sb.Length >= MaxExtractedChars)
                    break;
            }

            if (sb.Length == 0)
                return null;

            if (sb.Length > MaxExtractedChars)
                sb.Length = MaxExtractedChars;

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Adjuntos] No se pudo extraer texto digital del PDF.");
            return null;
        }
    }
}
