using System.Text.Json.Serialization;

namespace agentsupervisorpoc.Models;

public class WhatsAppCloudModel
{
    [JsonPropertyName("entry")]
    public List<WhatsAppEntry>? Entry { get; set; }
}

public class WhatsAppEntry
{
    [JsonPropertyName("changes")]
    public List<WhatsAppChange>? Changes { get; set; }
}

public class WhatsAppChange
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("value")]
    public WhatsAppValue? Value { get; set; }
}

public class WhatsAppValue
{
    [JsonPropertyName("messaging_product")]
    public string? MessagingProduct { get; set; }

    [JsonPropertyName("messages")]
    public List<WhatsAppMessage>? Messages { get; set; }

    [JsonPropertyName("contacts")]
    public List<WhatsAppContact>? Contacts { get; set; }
}

public class WhatsAppMessage
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public WhatsAppText? Text { get; set; }

    [JsonPropertyName("document")]
    public WhatsAppDocument? Document { get; set; }

    [JsonPropertyName("image")]
    public WhatsAppImage? Image { get; set; }

    [JsonPropertyName("interactive")]
    public WhatsAppInteractive? Interactive { get; set; }

    [JsonIgnore]
    public string DisplayFileName => Document?.FileName ?? Image?.DecodedFileName ?? string.Empty;

    [JsonIgnore]
    public string DocumentMediaId => Document?.Id ?? Image?.Id ?? string.Empty;

    [JsonIgnore]
    public string DocumentMimeType => Document?.MimeType ?? Image?.MimeType ?? string.Empty;
}

public class WhatsAppContact
{
    [JsonPropertyName("profile")]
    public WhatsAppProfile? Profile { get; set; }
}

public class WhatsAppProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class WhatsAppInteractive
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("list_reply")]
    public WhatsAppListReply? ListReply { get; set; }

    [JsonPropertyName("button_reply")]
    public WhatsAppButtonReply? ButtonReply { get; set; }
}

public class WhatsAppListReply
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class WhatsAppButtonReply
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public class WhatsAppText
{
    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

public class WhatsAppDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("filename")]
    public string? FileName { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

public class WhatsAppImage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonIgnore]
    public string DecodedFileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(MimeType)) return "imagen.jpg";
            if (MimeType.Contains("png", StringComparison.OrdinalIgnoreCase)) return "imagen.png";
            if (MimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
                MimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase)) return "imagen.jpg";
            return "imagen.jpg";
        }
    }
}
