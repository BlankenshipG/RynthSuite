using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RynthCore.LootEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;

        // Global save — File menu "Save" has no system gesture on all platforms; match Monster editor.
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S)
            {
                _vm.FileSave.Execute(null);
                e.Handled = true;
            }
        };

        Closing += async (_, e) =>
        {
            if (_forceClose || !_vm.IsDirty) return;
            e.Cancel = true;
            var dlg = new ConfirmDialog("Unsaved changes will be lost. Continue?");
            var result = await dlg.ShowDialog<bool>(this);
            if (result) { _forceClose = true; Close(); }
        };
    }
}
