namespace AI.MacroSystem.IntegrationTests;

using AI.MacroSystem.App.ViewModels;
using AI.MacroSystem.Application;
using AI.MacroSystem.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class DependencyInjectionTests
{
    [Fact]
    public void ServiceContainer_ShouldResolveRegisteredServicesAndViewModels()
    {
        var services = new ServiceCollection();

        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddSingleton<MainWindowViewModel>();

        var provider = services.BuildServiceProvider();

        var viewModel = provider.GetService<MainWindowViewModel>();
        viewModel.Should().NotBeNull();
        viewModel!.Title.Should().Be("AI Macro System");
        viewModel.StatusMessage.Should().Be("Ready");
    }
}
