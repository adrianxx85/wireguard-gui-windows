namespace WGClient;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Standalone-Edition: Windows startet WGClient.exe als Tunnel-Dienst ("/service <conf>") – ohne Oberfläche.
        if (args.Length == 2 && args[0] == Core.EmbeddedTunnel.ServiceSwitch)
            return Core.EmbeddedTunnel.IsAvailable ? Core.EmbeddedTunnel.RunService(args[1]) : 3;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
