using System.ComponentModel;
using agentsupervisorpoc.Application.Interfaces;
using agentsupervisorpoc.Infrastructure.Configuration;
using agentsupervisorpoc.Infrastructure.Foundry;
using Microsoft.Extensions.Logging;

namespace agentsupervisorpoc.Infrastructure.Plugins;

public sealed class FoundryAgentsPlugin
{
    private readonly FoundryAgentClient _client;
    private readonly IFoundryAgentRegistry _registry;
    private readonly ILogger<FoundryAgentsPlugin> _logger;

    public FoundryAgentsPlugin(FoundryAgentClient client, IFoundryAgentRegistry registry, ILogger<FoundryAgentsPlugin> logger)
    {
        _client = client;
        _registry = registry;
        _logger = logger;
    }

    [Description(
        "Delega al Agente de Validación Crediticia (Azure AI Foundry) la decisión de aprobación o denegación del crédito. " +
        "Pásale la ficha consolidada del cliente con todos los datos financieros disponibles.")]
    public async Task<string> ValidarCreditoAsync(
        [Description("Ficha consolidada del cliente: documento, ingresos, gastos, deudas/cuotas, monto solicitado, plazo, actividad del negocio y datos de la documentación")] string fichaCliente,
        CancellationToken cancellationToken = default)
    {
        var agent = _registry.Get(FoundryAgentRegistry.CreditValidationAgent);
        if (agent is null || !agent.IsConfigured)
        {
            _logger.LogWarning("[Delegacion] Agente '{Agent}' no configurado.", FoundryAgentRegistry.CreditValidationAgent);
            return "ERROR: el agente de validación crediticia no está configurado (FoundryAgents:CreditValidationAgent:AgentId). " +
                   "Realiza la evaluación manual según las reglas del prompt e indícalo en Observaciones.";
        }

        try
        {
            _logger.LogInformation("[Delegacion] Invocando al agente '{Agent}' con la ficha del cliente.",
                agent.AgentId);

            var output = await _client.RunAsync(agent.AgentId, fichaCliente, cancellationToken);
            if (string.IsNullOrWhiteSpace(output))
                return "ERROR: el agente de validación crediticia no devolvió contenido. Evalúa manualmente e indícalo en Observaciones.";

            return output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Delegacion] Falló la invocación al agente '{Agent}'.", agent.AgentId);
            return $"ERROR al invocar al agente de validación crediticia: {ex.Message}. " +
                   "Realiza la evaluación manual según las reglas del prompt e indícalo en Observaciones.";
        }
    }
}
