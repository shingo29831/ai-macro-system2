namespace AI.MacroSystem.App.Views;

using System.Windows;
using AI.MacroSystem.App.ViewModels;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
