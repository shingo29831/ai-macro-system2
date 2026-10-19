namespace AI.MacroSystem.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "AI Macro System";

    [ObservableProperty]
    private string _statusMessage = "Ready";
}
