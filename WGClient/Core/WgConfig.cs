using System.Text;

namespace WGClient.Core;

public sealed class WgEntry(string key, string value)
{
    public string Key { get; } = key;
    public string Value { get; } = value;
}

public sealed class WgSection(string name)
{
    public string Name { get; } = name;
    public List<WgEntry> Entries { get; } = [];

    static bool Is(WgEntry e, string key) => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Liefert den Wert; mehrfach vorkommende Schlüssel (z. B. mehrere DNS-Zeilen) werden zusammengeführt.</summary>
    public string? Get(string key)
    {
        var values = Entries.Where(e => Is(e, key)).Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
        return values.Count == 0 ? null : string.Join(", ", values);
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { Remove(key); return; }
        var idx = Entries.FindIndex(e => Is(e, key));
        if (idx < 0) { Entries.Add(new WgEntry(key, value)); return; }
        Entries[idx] = new WgEntry(Entries[idx].Key, value);
        for (var i = Entries.Count - 1; i > idx; i--)
            if (Is(Entries[i], key)) Entries.RemoveAt(i);
    }

    public void Remove(string key) => Entries.RemoveAll(e => Is(e, key));

    public WgSection Clone()
    {
        var s = new WgSection(Name);
        s.Entries.AddRange(Entries.Select(e => new WgEntry(e.Key, e.Value)));
        return s;
    }
}

/// <summary>Einfacher Parser/Serializer für WireGuard-.conf-Dateien (INI-Format).</summary>
public sealed class WgConfig
{
    public List<WgSection> Sections { get; } = [];

    public WgSection? Interface => Sections.FirstOrDefault(s => s.Name.Equals("Interface", StringComparison.OrdinalIgnoreCase));
    public IEnumerable<WgSection> Peers => Sections.Where(s => s.Name.Equals("Peer", StringComparison.OrdinalIgnoreCase));

    public static WgConfig Parse(string text)
    {
        var cfg = new WgConfig();
        WgSection? current = null;
        var lineNo = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            lineNo++;
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new WgSection(line[1..^1].Trim());
                cfg.Sections.Add(current);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Zeile {lineNo}: ungültiger Eintrag \"{raw.Trim()}\".");
            if (current is null) throw new FormatException($"Zeile {lineNo}: Eintrag steht außerhalb einer Sektion.");
            current.Entries.Add(new WgEntry(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return cfg;
    }

    public List<string> Validate()
    {
        var errors = new List<string>();
        var iface = Interface;
        if (iface is null) errors.Add("Sektion [Interface] fehlt.");
        else
        {
            if (iface.Get("PrivateKey") is null) errors.Add("PrivateKey fehlt in [Interface].");
            if (iface.Get("Address") is null) errors.Add("Address fehlt in [Interface].");
        }
        var peers = Peers.ToList();
        if (peers.Count == 0) errors.Add("Mindestens ein [Peer] wird benötigt.");
        if (peers.Any(p => p.Get("PublicKey") is null)) errors.Add("Ein [Peer] hat keinen PublicKey.");
        return errors;
    }

    public string Serialize(bool maskSecrets = false)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (i > 0) sb.AppendLine();
            sb.AppendLine($"[{s.Name}]");
            foreach (var e in s.Entries)
            {
                var secret = e.Key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)
                          || e.Key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase);
                sb.AppendLine($"{e.Key} = {(maskSecrets && secret ? "(verborgen)" : e.Value)}");
            }
        }
        return sb.ToString();
    }

    public WgConfig Clone()
    {
        var c = new WgConfig();
        c.Sections.AddRange(Sections.Select(s => s.Clone()));
        return c;
    }
}
