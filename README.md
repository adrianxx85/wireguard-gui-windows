<p align="center">
  <img src="WGClient/Assets/app.png" width="96" alt="WG Client Icon">
</p>

<h1 align="center">WG Client</h1>

<p align="center">
  Eine einfache Windows-Oberfläche für WireGuard-VPN-Tunnel.<br>
  <code>.conf</code> importieren, auf <b>Verbinden</b> klicken – fertig.
</p>

---

## Funktionen

- **Import** von WireGuard-Konfigurationen per Button, Drag & Drop oder „Öffnen mit“
- **Neuen Tunnel erstellen** per Assistent für Internet-VPN, Point-to-Site oder Point-to-Point. Der Assistent schlägt bei Verfügbarkeit eine freie private IPv4-Adresse vor und erzeugt das Schlüsselpaar. `[Peer]`-Blöcke lassen sich für die gewählte Gegenstelle kopieren, mit Schutz vor Windows-Zwischenablageverlauf und Synchronisierung.
- **Verbinden/Trennen** mit Live-Status: Handshake, empfangene/gesendete Daten, Endpoint
- **Optionen ohne Datei-Bearbeitung**
  - DNS-Server festlegen und bei Änderungen fehlende Tunnelrouten ergänzen
  - Gesamten Traffic durch den Tunnel **oder** Split-Tunnel für bestimmte Netze
  - Kill-Switch an/aus
  - Lokales Netzwerk (Drucker, NAS) erreichbar lassen
  - IPv6 mit tunneln (gegen IPv6-Leaks)
  - Endpoint, MTU, Keepalive
  - Beim Windows-Start automatisch verbinden
- **Vorschau** der effektiven Konfiguration und WireGuard-Log
- **Tray-Icon** – Tunnel laufen als Windows-Dienst weiter, auch wenn die App geschlossen ist
- Konfigurationen werden geschützt unter `C:\ProgramData\WGClient` abgelegt (nur Administratoren/SYSTEM)

## Installation

Unter [Releases](../../releases) das aktuelle `WGClient-Setup-x.y.z.exe` herunterladen und ausführen.
Fehlen [WireGuard für Windows](https://www.wireguard.com/install/) oder die .NET 10 Desktop Runtime, lädt das Setup sie automatisch nach.

> Das Setup ist nicht signiert. Zeigt Windows SmartScreen eine Warnung, auf **Weitere Informationen → Trotzdem ausführen** klicken.

Ausführliche Bedienungsanleitung: [ANLEITUNG.md](ANLEITUNG.md)

**Voraussetzungen:** Windows 10/11 (x64), Administratorrechte

## Wie es funktioniert

WG Client bringt keine eigene VPN-Implementierung mit, sondern steuert die offizielle WireGuard-Installation:
Beim Verbinden wird aus Original-Konfiguration + Optionen eine effektive `.conf` erzeugt und per
`wireguard.exe /installtunnelservice` als Tunnel-Dienst gestartet. Status und Traffic kommen von `wg show`.

## Selbst bauen

Benötigt: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) und [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`)

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Ergebnis: `publish\WGClient.exe` (App) und `dist\WGClient-Setup-<Version>.exe` (Installer).

| Pfad | Inhalt |
|---|---|
| `WGClient/Core` | Config-Parser, Routing-Berechnung (AllowedIPs), Speicher, WireGuard-Steuerung |
| `WGClient/ViewModels` | UI-Logik |
| `installer/WGClient.iss` | Inno-Setup-Skript |

## Unterstützen

Gefällt dir WG Client? Über einen Kaffee freue ich mich ☕

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-PayPal-F5A524?logo=paypal&logoColor=white)](https://paypal.me/AdrianZblewski)

---

<sub>WG Client ist ein inoffizielles Projekt und steht in keiner Verbindung zu WireGuard oder Jason A. Donenfeld. „WireGuard“ ist eine eingetragene Marke von Jason A. Donenfeld.</sub>
