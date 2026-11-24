using System.IO.Pipes;

namespace WGClient.Core;

/// <summary>
/// Client für den lokalen Manager-Dienst (<see cref="ManagerService"/>). Ermöglicht Standardbenutzern
/// das Anzeigen, Verbinden und Trennen von durch Administratoren eingerichteten Tunneln – ohne Zugriff auf Schlüssel.
/// </summary>
internal static class ManagerClient
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan OperationIoTimeout = TimeSpan.FromSeconds(90);

    public static async Task<bool> PingAsync(string pipeName = ManagerProtocol.PipeName, CancellationToken token = default)
    {
        try
        {
            var response = await SendAsync(new ManagerRequest("ping"), DefaultIoTimeout, pipeName, token).ConfigureAwait(false);
            return response.Ok;
        }
        catch { return false; }
    }

    public static async Task<List<TunnelSnapshot>> ListAsync(string pipeName = ManagerProtocol.PipeName, CancellationToken token = default)
    {
        var response = await SendAsync(new ManagerRequest("list"), DefaultIoTimeout, pipeName, token).ConfigureAwait(false);
        return response.Tunnels ?? [];
    }

    public static async Task<List<TunnelStateSnapshot>> GetStatesAsync(string pipeName = ManagerProtocol.PipeName, CancellationToken token = default)
    {
        var response = await SendAsync(new ManagerRequest("states"), DefaultIoTimeout, pipeName, token).ConfigureAwait(false);
        return response.States ?? [];
    }

    public static async Task<bool> ConnectAsync(string name, string pipeName = ManagerProtocol.PipeName, CancellationToken token = default)
    {
        var response = await SendAsync(new ManagerRequest("connect", name), OperationIoTimeout, pipeName, token).ConfigureAwait(false);
        return response.Running ?? false;
    }

    public static async Task DisconnectAsync(string name, string pipeName = ManagerProtocol.PipeName, CancellationToken token = default) =>
        await SendAsync(new ManagerRequest("disconnect", name), OperationIoTimeout, pipeName, token).ConfigureAwait(false);

    public static async Task<string> DumpLogAsync(string? tunnelFilter, string pipeName = ManagerProtocol.PipeName, CancellationToken token = default)
    {
        var response = await SendAsync(new ManagerRequest("log", tunnelFilter), DefaultIoTimeout, pipeName, token).ConfigureAwait(false);
        return response.Log ?? "";
    }

    static async Task<ManagerResponse> SendAsync(ManagerRequest request, TimeSpan ioTimeout, string pipeName, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(ConnectTimeout);
        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            throw new InvalidOperationException("Der WG-Client-Dienst ist nicht erreichbar. Bitte prüfe den Dienst „WG Client Manager“ oder starte WG Client als Administrator.", ex);
        }

        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        ioCts.CancelAfter(ioTimeout);
        await ManagerProtocol.WriteMessageAsync(pipe, request, ioCts.Token).ConfigureAwait(false);
        var response = await ManagerProtocol.ReadMessageAsync<ManagerResponse>(pipe, ManagerProtocol.MaxResponseBytes, ioCts.Token).ConfigureAwait(false)
            ?? throw new IOException("Der WG-Client-Dienst hat keine gültige Antwort gesendet.");
        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "Aktion wurde vom WG-Client-Dienst abgelehnt.");
        return response;
    }
}
