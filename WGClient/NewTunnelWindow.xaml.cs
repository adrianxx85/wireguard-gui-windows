using System.Windows;
using WGClient.ViewModels;

namespace WGClient;

public partial class NewTunnelWindow : Window
{
    readonly NewTunnelVm _vm;

    public NewTunnelWindow(Func<string, bool> nameExists, IEnumerable<string> tunnelAddresses)
    {
        InitializeComponent();
        _vm = new NewTunnelVm(nameExists, tunnelAddresses);
        DataContext = _vm;
    }

    /// <summary>Name und Konfiguration, wenn der Assistent mit "Tunnel erstellen" abgeschlossen wurde.</summary>
    public (string Name, string Config)? Result => _vm.Result;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.NextAsync()) { DialogResult = true; return; }
        if (_vm.IsStep1 && _vm.Name.Length == 0)
            _ = Dispatcher.BeginInvoke(() => NameBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void Back_Click(object sender, RoutedEventArgs e) => _vm.Back();

    void SuggestAddress_Click(object sender, RoutedEventArgs e) => _vm.SuggestAddress();

    async void GeneratePsk_Click(object sender, RoutedEventArgs e) => await _vm.GeneratePresharedKeyAsync();

    void CopySnippet_Click(object sender, RoutedEventArgs e) => Copy(_vm.ServerSnippetForCopy(), "[Peer]-Block");

    void CopyInterface_Click(object sender, RoutedEventArgs e) => Copy(_vm.InterfaceSuggestion, "[Interface]-Vorschlag");

    void Copy(string text, string what)
    {
        try
        {
            PrivateClipboard.SetText(text);
            _vm.Info = $"{what} in die Zwischenablage kopiert.";
        }
        catch (Exception ex) { _vm.Error = "Kopieren fehlgeschlagen: " + ex.Message; }
    }
}
