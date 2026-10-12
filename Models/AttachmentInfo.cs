namespace agentsupervisorpoc.Models;

public sealed class AttachmentInfo
{
    public string FileId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ExtractedText { get; init; }

    /// <summary>"pdf" o "imagen" (detectado por magic bytes).</summary>
    public string Kind { get; init; } = "pdf";

    public int ExtractedChars => ExtractedText?.Length ?? 0;
}
