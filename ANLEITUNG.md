WG Client – Anleitung
=====================

WG Client ist eine einfache Oberfläche für WireGuard-VPN-Tunnel unter Windows.
Du importierst deine WireGuard-Konfiguration (.conf), klickst auf "Verbinden" –
fertig. Optionen wie DNS, Kill-Switch oder Split-Tunnel lassen sich bequem
einstellen, ohne die Datei von Hand zu bearbeiten.


1. Installation
---------------

1. "WGClient-Setup-x.y.z.exe" starten und die Windows-Sicherheitsabfrage
   (Administratorrechte) bestätigen.
   Zeigt Windows "Der Computer wurde durch Windows geschützt" an:
   auf "Weitere Informationen" und dann "Trotzdem ausführen" klicken.
   (Das Setup ist nicht digital signiert.)
2. Den Schritten des Assistenten folgen.
3. Fehlen auf dem PC noch Komponenten, lädt das Setup sie automatisch
   herunter und installiert sie:
     - .NET 10 Desktop Runtime (von Microsoft)
     - WireGuard für Windows (von wireguard.com)
   Dafür wird eine Internetverbindung benötigt.

Voraussetzungen: Windows 10 oder 11 (64 Bit), Administratorrechte.


2. Erste Schritte
-----------------

1. WG Client starten (Startmenü oder Desktop). Die App fragt jedes Mal nach
   Administratorrechten – das ist nötig, weil sie VPN-Tunnel einrichtet.
2. Auf "+ Konfiguration importieren" klicken und deine .conf-Datei wählen.
   Alternativ: die .conf-Datei einfach in das Fenster ziehen, oder per
   Rechtsklick auf die Datei -> "Öffnen mit" -> "WG Client".
3. Auf "Verbinden" klicken.
   Der Punkt neben dem Tunnelnamen zeigt den Status:
     grün  = verbunden (Handshake mit dem Server erfolgreich)
     gelb  = Tunnel aktiv, wartet auf Antwort vom Server
     grau  = getrennt
4. Zum Trennen auf "Trennen" klicken.


3. Die Bereiche im Überblick
----------------------------

Übersicht
  Letzter Handshake, empfangene/gesendete Daten, Endpoint, Tunnel-Adresse,
  DNS und Routing auf einen Blick.

Optionen
  Hier passt du den Tunnel an. Änderungen mit "Speichern" übernehmen –
  ist der Tunnel gerade verbunden, wird er automatisch neu verbunden.

Konfiguration
  Zeigt die Konfiguration, mit der der Tunnel tatsächlich gestartet wird
  (private Schlüssel ausgeblendet). "Exportieren" speichert sie als .conf.

Log
  Das WireGuard-Protokoll – hilfreich, wenn eine Verbindung nicht klappt.


4. Optionen erklärt
-------------------

DNS
  Welche DNS-Server (Namensauflösung) im Tunnel genutzt werden. Mehrere
  mit Komma trennen, z. B. "192.168.0.10, 1.1.1.1". Leer lassen, um den
  normalen DNS von Windows zu verwenden. Die angegebenen DNS-Server werden
  automatisch durch den Tunnel geleitet.

Gesamten Traffic durch den Tunnel leiten
  Alles – Internet und alle Netze – läuft über den VPN-Server.
  Dazu gibt es drei Schalter:

  - Lokales Netzwerk erreichbar lassen
    Drucker, NAS usw. im Netz, in dem du gerade bist, bleiben erreichbar.
    Liegt dein Heimnetz (das hinter dem VPN) ebenfalls in einem privaten
    Bereich wie 192.168.x.x, trage es unter "Private Netze, die trotzdem
    durch den Tunnel sollen" ein, z. B. "192.168.0.0/24".

  - Kill-Switch
    Blockiert jeglichen Internetverkehr außerhalb des Tunnels, solange der
    Tunnel aktiv ist. So kann nichts "vorbei" gehen, falls die Verbindung
    hakt. Nicht gleichzeitig mit "Lokales Netzwerk erreichbar" möglich.

  - IPv6 ebenfalls durch den Tunnel leiten
    Verhindert, dass IPv6-Verbindungen am VPN vorbeigehen. Unterstützt
    dein VPN-Server kein IPv6, ist IPv6 dann einfach blockiert (das ist
    in der Regel kein Problem).

Nur bestimmte Netze (Split-Tunnel)
  Nur die angegebenen Netze laufen über das VPN, normales Surfen geht
  direkt ins Internet. Beispiel: "192.168.0.0/24" für das Heimnetz.

Endpoint, MTU, Keepalive
  - Endpoint: Adresse und Port des VPN-Servers (host:port).
  - MTU: Paketgröße; leer lassen = automatisch. Bei Verbindungsproblemen
    mit großen Downloads hilft oft 1280 oder 1380.
  - Keepalive: hält die Verbindung hinter Routern offen. 25 Sekunden ist
    ein guter Wert; leer oder 0 = aus.

Beim Windows-Start automatisch verbinden
  Der Tunnel startet zusammen mit Windows – auch ohne Anmeldung und ohne
  dass WG Client geöffnet ist.


5. Gut zu wissen
----------------

- Fenster schließen beendet die App nicht: WG Client läuft im Infobereich
  (Tray, unten rechts neben der Uhr) weiter. Beenden per Rechtsklick auf das
  Symbol -> "Beenden".
- Verbundene Tunnel bleiben aktiv, auch wenn WG Client beendet wird – sie
  laufen als Windows-Dienst. Trennen kannst du sie jederzeit wieder in der App.
- Importierte Konfigurationen werden sicher unter
  C:\ProgramData\WGClient gespeichert (nur für Administratoren lesbar),
  da sie deinen privaten Schlüssel enthalten. Die Originaldatei kannst du
  danach löschen oder sicher aufbewahren.
- Denselben Tunnel nicht gleichzeitig in der offiziellen WireGuard-App und
  in WG Client verbinden.


6. Probleme?
------------

Status bleibt gelb ("warte auf Handshake")
  Der Server antwortet nicht. Prüfen: Internetverbindung, Endpoint-Adresse
  und Port, Portfreigabe am Router (UDP), Uhrzeit des PCs.

Keine Webseiten erreichbar, obwohl verbunden
  Meist ein DNS-Problem: DNS-Feld prüfen oder testweise "1.1.1.1" eintragen.

Drucker/NAS zu Hause nicht erreichbar
  "Lokales Netzwerk erreichbar lassen" aktivieren.

Weitere Hinweise liefert der Reiter "Log" (Button "Aktualisieren").


7. Deinstallation
-----------------

Windows-Einstellungen -> Apps -> Installierte Apps -> "WG Client" ->
Deinstallieren. Von WG Client verbundene Tunnel werden dabei getrennt.
Du wirst gefragt, ob die gespeicherten Konfigurationen ebenfalls gelöscht
werden sollen. WireGuard selbst bleibt installiert und kann separat
entfernt werden.


8. Unterstützen
---------------

Gefällt dir WG Client? Über einen Kaffee freue ich mich:
https://paypal.me/AdrianZblewski  ☕
(auch über den Button "Buy me a coffee" in der App)


9. Für Entwickler: selbst bauen
-------------------------------

Benötigt: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)

  powershell -ExecutionPolicy Bypass -File build.ps1

Ergebnis:
  publish\WGClient.exe              die App (eine Datei)
  dist\WGClient-Setup-<Version>.exe der Installer

Die Version wird in WGClient\WGClient.csproj (<Version>) gepflegt.
