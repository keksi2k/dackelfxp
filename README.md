<img src="tools/icon/dackelfxp-1024.png" alt="DackelFXP" width="128" align="right">

# DackelFXP

**English** | [Deutsch](README.de.md)

FTP/FTPS/FXP client in C# / .NET 10 with an Avalonia UI – runs on Windows and Linux (tested: Windows 11, Ubuntu 24.04).
Modeled after FlashFXP: two browser panes, site manager, transfer queue and site-to-site transfers including secure FXP.

## Features

- **Protocols:** FTP, FTPS explicit (`AUTH TLS`) and implicit (port 990), `PROT P/C`, `CCC`, TLS 1.2/1.3
  - Automatic fallback to TLS 1.2 when the server requires session reuse on data connections (e.g. vsftpd `require_ssl_reuse`)
  - Certificate check with a dialog; fingerprints can be trusted per site
- **FXP:** site to site, **secure FXP via SSCN (PASV or PORT side) and CPSV**, `PRET` (drftpd), resume via `REST`, fallback to the opposite PASV direction. The working variant is remembered per site pair.
- **Data connections:** PASV, EPSV, PORT, EPRT, port range and external IP for active mode, NAT correction for private PASV addresses
- **Listings:** MLSD, `LIST -al`, `STAT -l`; parsers for Unix (incl. glftpd/drftpd), Windows/IIS and MLSD; time zone and character set per site
- **Queue:** download, upload and FXP, folders are expanded recursively, several parallel slots, retries with delay, speed limits (global and per site), save and load, pause, reorder
- **File exists:** separate rules for a smaller, equal or larger target (resume, skip, overwrite, rename, ask)
- **Skiplists / priorities:** wildcards and regex, e.g. `.sfv`/`.nfo` first, `Sample` last
- **Tools:** folder comparison with highlighting, "queue differences", SFV check (CRC32), custom commands with placeholders (`%p %f %d %s %1`), raw commands, CHMOD, view/edit with automatic upload after saving, scheduler (start/stop the queue at set times), ident server
- **Command line** below the log: raw and custom commands to the site of the left or right pane; replies appear in the log (history with ↑/↓)
- **Security:** passwords encrypted with AES-GCM, optionally protected by a master password (PBKDF2)
- **UI:** drag & drop between panes and from the system file manager, bookmarks, filter, colored protocol log per connection, dark/light theme, configurable font (default Consolas) and list font size, optional "sort all links on top", German/English (switchable live under *Tools → Language*)

## Keyboard shortcuts

| Key | Action |
|---|---|
| F4 / F8 | Site manager / quick connect |
| Enter, Backspace | Open, go up one level |
| Ctrl+T / Ctrl+Q | Transfer / add to queue |
| F2, F3, F5, F7, Del | Rename, view/edit, refresh, new folder, delete |
| Ctrl+L | Path bar |
| Ctrl+M | Compare folders |
| Ctrl+K | Command line |
| F9 / Shift+F9 | Start / stop queue |
| Middle mouse button | One folder up |
| Mouse "back" button | Previous folder |
| Drag & drop | Between the panes: download, upload or FXP |
| Typing in the list | Jumps to the first entry starting with the typed text (same letter again: next match) |
| Right-click on a column header | Show/hide columns; drag a header to move the column |

## Build and run

```bash
dotnet build
dotnet run --project src/DackelFxp.App
```

Command line options:

- `--config <folder>` (or environment variable `DACKELFXP_CONFIG`): separate configuration folder, e.g. for a portable installation
- `--left <site>` / `--right <site>`: connect to a site on startup

Default configuration folder: `%APPDATA%\DackelFxp` (Windows) or `~/.config/dackelfxp` (Linux).

### Publish (single file, no .NET runtime required)

```bash
dotnet publish src/DackelFxp.App -p:PublishProfile=win-x64     # -> artifacts/win-x64/DackelFxp.exe
dotnet publish src/DackelFxp.App -p:PublishProfile=linux-x64   # -> artifacts/linux-x64/DackelFxp
```

On Ubuntu the usual X11 libraries are required (`libx11-6 libice6 libsm6 libfontconfig1`); they are present on any desktop system. Consolas is usually not installed on Linux; the UI then falls back to DejaVu Sans Mono or Liberation Mono.

## Tests

```bash
dotnet test                       # unit tests incl. an in-process FTP server (TLS, SSCN, CPSV, PRET)
                                  # and headless UI tests (drag & drop, mouse buttons)
```

Integration tests on Ubuntu against two real vsftpd servers (FTPS with `require_ssl_reuse`, FXP, resume):

```bash
docker compose -f tests/DackelFxp.IntegrationTests/docker/docker-compose.yml up --build --abort-on-container-exit --exit-code-from tests
```

For trying out the UI manually there are two test servers (FTP on port 2121, FTPS on port 2122):

```bash
dotnet run --project tools/DackelFxp.DevServer
```

## Project structure

```
src/DackelFxp.Ftp     Protocol stack (FtpClient, FxpTransfer, listing parsers) – no UI dependencies
src/DackelFxp.Core    Sites, encryption, connection pool, queue/transfer engine, rules, scheduler, tools
src/DackelFxp.App     Avalonia UI (MVVM)
tests/                Unit and integration tests
tools/                Dev FTP server
```

## Known limitations

- No SFTP and no proxy/bouncer support
- Secure FXP (SSCN/CPSV) is tested against the built-in test server, but not against real glftpd/drftpd/ioFTPD servers
- FXP transfers show no byte progress, only the elapsed time (the data flows directly between the servers)
- No import of FlashFXP sites (`Sites.dat`)
