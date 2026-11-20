namespace WGClient;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "/verify-service-files")
        {
            try
            {
                var executable = Path.GetFullPath(args[1]);
                var directory = Path.GetDirectoryName(executable)!;
                Core.ServiceFiles.Verify(executable, Path.Combine(directory, "tunnel.dll"), Path.Combine(directory, "wireguard.dll"));
                return 0;
            }
            catch (Exception) { return 2; }
        }

        // Standalone-Edition: Windows startet WGClient.exe als Tunnel-Dienst ("/service <conf>") – ohne Oberfläche.
        if (args.Length == 2 && args[0] == Core.EmbeddedTunnel.ServiceSwitch)
            return Core.EmbeddedTunnel.IsAvailable ? Core.EmbeddedTunnel.RunService(args[1]) : 3;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
