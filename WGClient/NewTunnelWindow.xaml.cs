using System.ComponentModel;
using System.Windows;
using WGClient.ViewModels;

namespace WGClient;

public partial class NewTunnelWindow : Window
{
    readonly NewTunnelVm _vm;
    readonly Action<string, string> _create;
    bool _saved;

    /// <param name="create">Speichert den fertigen Tunnel (Name, Konfiguration). Wirft bei Fehlern –
    /// der Assistent bleibt dann offen, damit das Schlüsselpaar nicht verloren geht.</param>
    public NewTunnelWindow(Func<string, bool> nameExists, IEnumerable<string> tunnelAddresses, Action<string, string> create)
    {
        InitializeComponent();
        _create = create;
        _vm = new NewTunnelVm(nameExists, tunnelAddresses);
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _saved || !_vm.HasUnsavedGeneratedKey) return;
        var answer = MessageBox.Show(this,
            "Der Tunnel wurde noch nicht erstellt. Beim Schließen geht das erzeugte Schlüsselpaar verloren – " +
            "ein bereits weitergegebener öffentlicher Schlüssel ist dann nutzlos.\n\nAssistent trotzdem schließen?",
            "Neuen Tunnel erstellen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.NextAsync())
        {
            if (_vm.Result is not { } result) return;
            try
            {
                _create(result.Name, result.Config);
            }
            catch (Exception ex)
            {
                _vm.Error = "Tunnel konnte nicht erstellt werden: " + ex.Message;
                return;
            }
            _saved = true;
            DialogResult = true;
            return;
        }
        if (_vm.IsStep1 && _vm.Name.Trim().Length == 0)
            _ = Dispatcher.BeginInvoke(() => NameBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void Back_Click(object sender, RoutedEventArgs e) => _vm.Back();

    void SuggestAddress_Click(object sender, RoutedEventArgs e) => _vm.SuggestAddress();

    async void GeneratePsk_Click(object sender, RoutedEventArgs e) => await _vm.GeneratePresharedKeyAsync();

    // Der öffentliche Schlüssel ist nicht geheim: normale Zwischenablage (inkl. Verlauf/Synchronisierung zum Weitergeben).
    void CopyPublicKey_Click(object sender, RoutedEventArgs e) =>
        Copy(() => Clipboard.SetDataObject(_vm.PublicKey, copy: true), "Öffentlicher Schlüssel");

    void CopySnippet_Click(object sender, RoutedEventArgs e) =>
        Copy(() => PrivateClipboard.SetText(_vm.ServerSnippetForCopy()), "[Peer]-Block");

    void CopyInterface_Click(object sender, RoutedEventArgs e) =>
        Copy(() => PrivateClipboard.SetText(_vm.InterfaceSuggestion), "[Interface]-Vorschlag");

    void Copy(Action copy, string what)
    {
        try
        {
            copy();
            _vm.Info = $"{what} in die Zwischenablage kopiert.";
        }
        catch (Exception ex) { _vm.Error = "Kopieren fehlgeschlagen: " + ex.Message; }
    }
}
