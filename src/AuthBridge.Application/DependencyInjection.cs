using AuthBridge.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuthBridge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddAuthBridgeApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISimulationSignal, NoSimulationSignal>();
        services.Configure<WorkflowOptions>(configuration.GetSection(WorkflowOptions.Section));
        services.Configure<SimulationOptions>(configuration.GetSection(SimulationOptions.Section));

        services.AddScoped<ICallerContextResolver, CallerContextResolver>();
        services.AddScoped<IAuthorizationQueryService, AuthorizationQueryService>();
        services.AddScoped<IRequirementQueryService, RequirementQueryService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IAuthorizationWorkflowService, AuthorizationWorkflowService>();
        services.AddScoped<ISubmissionProposalService, SubmissionProposalService>();
        services.AddScoped<ISubmissionService, SubmissionService>();
        services.AddScoped<IPayerSimulationService, PayerSimulationService>();
        return services;
    }
}
