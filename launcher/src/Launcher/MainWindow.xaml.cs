using System.Windows;
using LeafItAlone.Launcher.ViewModels;

namespace LeafItAlone.Launcher;

/// <summary>
/// Shell window. All state lives in <see cref="MainViewModel"/>; the code-behind
/// only sets the DataContext (keeps the XAML binding targets honest).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
