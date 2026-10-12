
namespace agentsupervisorpoc.Application.Services;

public static class WelcomeService
{
    public static string Build(IConfiguration config)
    {
        var texto = config["MensajesBienvenida:Saludo"]
            ?? "Soy tu asistente de evaluación de crédito. ¿Empezamos?";

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
}
