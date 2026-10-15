<img src="tools/icon/dackelfxp-1024.png" alt="DackelFXP" width="128" align="right">

# DackelFXP

[English](README.md) | **Deutsch**

FTP/FTPS/FXP-Client in C# / .NET 10 mit Avalonia-Oberfläche – läuft auf Windows und Linux (getestet: Windows 11, Ubuntu 24.04).
Vorbild ist FlashFXP: zwei Browser-Seiten, Site-Manager, Queue und Site-zu-Site-Transfers inklusive Secure FXP.

## Funktionen

- **Protokolle:** FTP, FTPS explizit (`AUTH TLS`) und implizit (Port 990), `PROT P/C`, `CCC`, TLS 1.2/1.3
  - Automatischer Wechsel auf TLS 1.2, wenn der Server Session-Reuse auf der Datenverbindung verlangt (z. B. vsftpd `require_ssl_reuse`)
  - Zertifikatsprüfung mit Dialog, Fingerprint pro Site speicherbar
- **FXP:** Site-zu-Site, **Secure FXP per SSCN (PASV- oder PORT-Seite) und CPSV**, `PRET` (drftpd), Resume per `REST`, Fallback auf die andere PASV-Richtung. Die funktionierende Variante wird pro Site-Paar gemerkt.
- **Datenverbindung:** PASV, EPSV, PORT, EPRT, Port-Bereich und externe IP für aktiven Modus, NAT-Korrektur bei privaten PASV-Adressen
- **Listing:** MLSD, `LIST -al`, `STAT -l`; Parser für Unix (inkl. glftpd/drftpd), Windows/IIS und MLSD; Zeitzone und Zeichensatz pro Site
- **Queue:** Download, Upload und FXP, Ordner werden rekursiv aufgelöst, mehrere parallele Slots, Retry mit Pause, Speedlimit (global und pro Site), Speichern und Laden, Pausieren, Umsortieren
- **Datei existiert:** eigene Regel für Ziel kleiner, gleich groß oder größer (Fortsetzen, Überspringen, Überschreiben, Umbenennen, Nachfragen)
- **Skiplisten / Prioritäten:** Wildcard und Regex, z. B. `.sfv`/`.nfo` zuerst, `Sample` zuletzt
- **Werkzeuge:** Ordnervergleich mit Markierung, „Unterschiede in Queue", SFV-Prüfung (CRC32), Benutzerbefehle mit Platzhaltern (`%p %f %d %s %1`), Raw-Befehle, CHMOD, Anzeigen/Bearbeiten mit automatischem Upload nach dem Speichern, Planer (Queue zeitgesteuert starten/stoppen), Ident-Server
- **Sicherheit:** Passwörter AES-GCM-verschlüsselt, optional mit Master-Passwort (PBKDF2)
- **Oberfläche:** Drag & Drop zwischen den Seiten und aus dem Dateimanager, Lesezeichen, Filter, farbiges Protokoll pro Verbindung, helles/dunkles Design, einstellbare Schriftart (Standard Consolas) und Listen-Schriftgröße, optional Links oben sortieren, Deutsch/Englisch (live umschaltbar unter *Werkzeuge → Sprache*)
- **Befehlszeile** unter dem Log: Raw- und eigene Befehle an die Site der linken oder rechten Seite, Antworten erscheinen im Log (Verlauf mit ↑/↓)

## Tastenkürzel

| Taste | Aktion |
|---|---|
| F4 / F8 | Site-Manager / Schnellverbindung |
| Enter, Backspace | Öffnen, eine Ebene höher |
| Ctrl+T / Ctrl+Q | Übertragen / in die Queue |
| F2, F3, F5, F7, Entf | Umbenennen, Anzeigen/Bearbeiten, Aktualisieren, Neuer Ordner, Löschen |
| Ctrl+L | Pfadzeile |
| Ctrl+M | Ordner vergleichen |
| Ctrl+K | Befehlszeile |
| F9 / Shift+F9 | Queue starten / stoppen |
| Mittlere Maustaste | Einen Ordner höher |
| Maus-Zurück-Taste | Vorheriger Ordner |
| Drag & Drop | Zwischen den Seiten: Download, Upload oder FXP |

## Bauen und starten

```bash
dotnet build
dotnet run --project src/DackelFxp.App
```

Programmoptionen:

- `--config <ordner>` (oder Umgebungsvariable `DACKELFXP_CONFIG`): eigener Konfigurationsordner, z. B. für eine portable Installation
- `--left <site>` / `--right <site>`: beim Start mit einer Site verbinden

Standard-Konfiguration: `%APPDATA%\DackelFxp` (Windows) bzw. `~/.config/dackelfxp` (Linux).

### Veröffentlichen (Single-File, ohne installierte .NET-Runtime)

```bash
dotnet publish src/DackelFxp.App -p:PublishProfile=win-x64     # -> artifacts/win-x64/DackelFxp.exe
dotnet publish src/DackelFxp.App -p:PublishProfile=linux-x64   # -> artifacts/linux-x64/DackelFxp
```

Unter Ubuntu werden die üblichen X11-Bibliotheken benötigt (`libx11-6 libice6 libsm6 libfontconfig1`), auf einem Desktop-System sind sie vorhanden. Consolas ist unter Linux meist nicht installiert; dann wird DejaVu Sans Mono bzw. Liberation Mono verwendet.

## Tests

```bash
dotnet test                       # Unit-Tests inkl. In-Process-FTP-Server (TLS, SSCN, CPSV, PRET)
                                  # und Headless-UI-Tests (Drag & Drop, Maustasten)
```

Integrationstests auf Ubuntu gegen zwei echte vsftpd-Server (FTPS mit `require_ssl_reuse`, FXP, Resume):

```bash
docker compose -f tests/DackelFxp.IntegrationTests/docker/docker-compose.yml up --build --abort-on-container-exit --exit-code-from tests
```

Zum manuellen Ausprobieren der Oberfläche gibt es zwei Test-Server (FTP auf Port 2121, FTPS auf Port 2122):

```bash
dotnet run --project tools/DackelFxp.DevServer
```

## Projektstruktur

```
src/DackelFxp.Ftp     Protokoll-Stack (FtpClient, FxpTransfer, Listing-Parser) – ohne UI-Abhängigkeiten
src/DackelFxp.Core    Sites, Verschlüsselung, Connection-Pool, Queue/Transfer-Engine, Regeln, Planer, Tools
src/DackelFxp.App     Avalonia-GUI (MVVM)
tests/                Unit- und Integrationstests
tools/                Dev-FTP-Server
```

## Bekannte Grenzen

- Kein SFTP und keine Proxy-/Bouncer-Unterstützung
- Secure FXP (SSCN/CPSV) ist gegen den eingebauten Test-Server geprüft, aber nicht gegen echte glftpd/drftpd/ioFTPD-Server
- FXP-Transfers zeigen keinen Byte-Fortschritt, nur die Laufzeit (die Daten fließen direkt zwischen den Servern)
- Kein Import von FlashFXP-Sites (`Sites.dat`)
