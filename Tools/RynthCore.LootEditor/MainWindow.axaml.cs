using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RynthCore.LootEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow() : this(null) { }

    /// <param name="openPath">A profile to open at startup (from the command line), or null.</param>
    public MainWindow(string? openPath)
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
        if (!string.IsNullOrWhiteSpace(openPath))
            _vm.OpenFromCommandLine(openPath);

        // Global save — File menu "Save" has no system gesture on all platforms; match Monster editor.
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S)
            {
                _vm.FileSave.Execute(null);
                e.Handled = true;
            }
        };

        // Confirm-on-close disabled: dirty flag is private during VTank model migration.
        _ = _forceClose; // silence unused-field warning
    }
}
