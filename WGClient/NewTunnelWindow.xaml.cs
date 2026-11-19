using System.Windows;
using WGClient.ViewModels;

namespace WGClient;

public partial class NewTunnelWindow : Window
{
    readonly NewTunnelVm _vm;

    public NewTunnelWindow(Func<string, bool> nameExists)
    {
        InitializeComponent();
        _vm = new NewTunnelVm(nameExists);
        DataContext = _vm;
        Loaded += (_, _) => NameBox.Focus();
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
        if (await _vm.NextAsync()) DialogResult = true;
    }

    void Back_Click(object sender, RoutedEventArgs e) => _vm.Back();

    async void GeneratePsk_Click(object sender, RoutedEventArgs e) => await _vm.GeneratePresharedKeyAsync();

    void CopySnippet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.ServerSnippet);
            _vm.Info = "Block in die Zwischenablage kopiert.";
        }
        catch (Exception ex) { _vm.Error = "Kopieren fehlgeschlagen: " + ex.Message; }
    }
}
