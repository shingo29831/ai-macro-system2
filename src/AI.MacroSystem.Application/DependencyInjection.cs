namespace AI.MacroSystem.Application;

using Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<AI.MacroSystem.Domain.Workflow.Actions.IActionTypeRegistry>(
            AI.MacroSystem.Domain.Workflow.Actions.ActionTypeRegistry.CreateDefault());
        services.AddSingleton<AI.MacroSystem.Application.Workflow.Serialization.WorkflowIrJsonSerializer>();
        services.AddSingleton<AI.MacroSystem.Application.Workflow.Validation.IWorkflowSchemaValidator,
            AI.MacroSystem.Application.Workflow.Validation.WorkflowSchemaValidator>();
        services.AddSingleton<AI.MacroSystem.Application.Workflow.Validation.Settings.CentralLoopSettings>();
        services.AddSingleton<AI.MacroSystem.Application.Workflow.Validation.ActionValidation.IActionParameterValidator,
            AI.MacroSystem.Application.Workflow.Validation.ActionValidation.DefaultActionParameterValidator>();
        services.AddSingleton<AI.MacroSystem.Application.Workflow.Validation.IWorkflowValidator,
            AI.MacroSystem.Application.Workflow.Validation.WorkflowValidator>();

        return services;
    }
}
