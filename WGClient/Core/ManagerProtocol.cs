using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WGClient.Core;

internal static class ManagerProtocol
{
    public const string ServiceName = "WGClientManager";
    public const string PipeName = "WGClientManager";
    public const int MaxRequestBytes = 4 * 1024;
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteMessageAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var line = new byte[payload.Length + 1];
        payload.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T?> ReadMessageAsync<T>(Stream stream, int maxBytes, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, token).ConfigureAwait(false);
            if (read == 0)
            {
                if (buffer.Length == 0) return default;
                break;
            }
            if (single[0] == (byte)'\n') break;
            if (buffer.Length >= maxBytes)
                throw new InvalidDataException("Nachricht überschreitet die zulässige Größe.");
            buffer.WriteByte(single[0]);
        }
        if (buffer.Length == 0) return default;
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return JsonSerializer.Deserialize<T>(text, Json);
    }
}

internal sealed record ManagerRequest(string Command, string? Name = null);

internal sealed record TunnelSnapshot(
    string Name,
    TunnelOptions Options,
    string AddressText,
    int PeerCount,
    bool UsesManualRoutes,
    bool SupportsKillSwitch,
    string RoutingSummary,
    string AllowedIpsPreview,
    string EffectivePreview,
    TunnelState State);

internal sealed record TunnelStateSnapshot(
    string Name,
    TunnelState State,
    TunnelStats? Stats);

internal sealed record ManagerResponse(
    bool Ok,
    string? Error = null,
    bool? Running = null,
    TunnelState? State = null,
    List<TunnelSnapshot>? Tunnels = null,
    List<TunnelStateSnapshot>? States = null,
    string? Log = null);
