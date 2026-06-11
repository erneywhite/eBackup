# eBackup

> 🌐 **Languages:** [Русский](README.md) · **English** (this file)

**eBackup** is a modular Windows backup app: it backs up application settings and
any folders you pick, stores archives in several places at once — from a local
folder or NAS to Google Drive — and restores everything back into place, even on
another PC or after an OS reinstall. Branded dark UI plus a full CLI.

Free, open source, no subscriptions. If eBackup turns out useful, you can
[buy the author a coffee ☕](https://dalink.to/toristarm).

> 🚧 Heading towards the v0.1 release, but everything below **already works**.

## Storage — 7 kinds, as many as you like at once

| Kind | Details |
|---|---|
| 📁 Folder / network drive | UNC paths `\\nas\share`, optional SMB credentials (temporary connection, password in DPAPI) |
| 🔑 SFTP | password or a private key pasted **as content** (stored encrypted); remote folder picking via a **tree** |
| 📡 FTP / FTPS | FluentFTP; self-signed NAS certificates are accepted |
| 🪣 S3-compatible | AWS S3, MinIO, Backblaze B2, Cloudflare R2…; path-style addressing, prefix "folders" |
| ☁️ WebDAV | Nextcloud, ownCloud, Yandex.Disk etc. (app passwords) |
| 🟢 Google Drive | OAuth sign-in via the browser; `drive.file` scope — the app sees **only its own** files |
| 🔵 Dropbox | OAuth sign-in; isolated app folder |

Every storage gets a Test button, automatic availability badges ✓/✕ and a
used-space counter on the Overview.

## Features

**Backup:**
- modules (OBS Studio) + **any custom folders**; several destinations per run
- optional encryption; machine name in the archive name; compression level choice
- ✅ **Verification after every backup**: the archive is fully re-read
  (decompression validates CRC32), files are checked against their manifest
  SHA-256, and every destination is verified by size (folders get a full
  SHA-256 of the copy). Corrupted data never reaches a single storage.
- free-space checks **before** starting — a clean refusal instead of a mid-copy crash
- a liquid progress bar: water with honest physics, splashes and bubbles 🌊

**Schedules:** daily / weekdays / every N hours / **once a day when the PC is
idle** (no input and the system is calm). Each schedule owns its module set,
folders, destinations and encryption; there is a "Run now" button. Schedules
work while the app is running — the tray is enough.

**Archives and the archive browser:**
- listings across all storages: size, date, 🔒 for encrypted; confirmed deletion
- **"Open"** shows a checkbox tree of the archive contents: restore the selected
  files to their original paths **or just download them** into any folder
- remote archives are read **in ranges** (SFTP seek / HTTP Range): a 100+ GB
  cloud archive opens its table of contents in seconds, and only the selected
  files are actually transferred

**Restore:** to the original locations or to any folder you choose; conflict
modes (replace with `.bak` / overwrite / add missing only); external OBS scene
assets land in a folder you pick and scene paths are rewritten automatically.

**History:** a journal of every operation — backups (manual and scheduled),
restores, extractions. Each run keeps a full log with millisecond timecodes:
every file with its size, per-target upload speeds, verification, errors.
Interrupted runs are honestly marked as such.

**Also nice:** result notifications (✅/❌), tray + autostart with Windows,
keep-last-N retention on all destinations, temp cleanup, quick links to
configs/logs from Settings.

**CLI** — everything is scriptable: `backup`, `restore`, `storage-list/test/ls`,
`module-add`; the passphrase for automation goes through the
`EBACKUP_PASSPHRASE` environment variable.

## Modules

- 🎬 **OBS Studio** (built-in): configuration without caches and logs, plugins
  from the install folder, and **dependent scene assets** (background images,
  videos etc. living outside OBS) — with path rewriting on restore
- 🧩 **Declarative drop-in modules**: drop a `*.module.json` into
  `%APPDATA%\eBackup\modules` (or `ebackup module-add file`) — and the app
  starts backing up the described paths. No code, no rebuild.

```json
{
  "id": "myapp",
  "displayName": "My App",
  "entries": [
    { "tokenPath": "{APPDATA}/MyApp", "type": "Directory",
      "archivePath": "config", "excludeGlobs": ["**/Cache/**"] }
  ]
}
```

## Security

- 🔐 Optional archive encryption: chunked **AES-256-GCM**, key derived from the
  passphrase via **Argon2id** — safe to put on servers you don't own
- 🔑 Connection secrets (passwords, keys, OAuth tokens) go through **Windows
  DPAPI**: never stored in plain text and intentionally **not portable** to
  another PC
- 🛡️ Declarative modules are restricted to app-data paths (no `.ssh`/keys/
  Program Files), archives are checked against path traversal, and every
  backup is verified before it ships

**The `.ebk` archive format** is ZIP + `manifest.json` with tokenized paths
(`{APPDATA}` etc.), which makes archives **portable between machines**. Names
are descriptive and sortable: `ebackup_MY-PC_obs-folders_2026-06-11_19-40-01.ebk`.

## Build and run

Requires the [.NET SDK 9.0+](https://dotnet.microsoft.com/download) (Windows 10/11).

```powershell
dotnet build eBackup.sln
dotnet test  eBackup.sln

# GUI
.\src\eBackup.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\eBackup.App.exe

# CLI
dotnet run --project src/eBackup.Cli -- list-modules
dotnet run --project src/eBackup.Cli -- backup --out .\backups --encrypt
dotnet run --project src/eBackup.Cli -- restore --archive .\backups\<name>.ebk --to C:\Temp\check
```

## Architecture

| Project | Purpose |
|---|---|
| `eBackup.Abstractions` | Plugin contract (interfaces, manifest model) — a stable boundary |
| `eBackup.Core` | Backup/restore engine with verification, encryption, module registry, schedules, the History journal |
| `eBackup.Security` | Secret protection via Windows DPAPI |
| `eBackup.Storage` | Unified storage model: folders/SMB, FTP/FTPS, S3, WebDAV, Google Drive, Dropbox; OAuth (PKCE + loopback); ranged reads of remote archives |
| `eBackup.Storage.Sftp` | SFTP on SSH.NET (+ seekable streams), `eBackup.Storage.Local` — the basic local provider |
| `eBackup.Modules.Obs` | The OBS Studio module (config + plugins + scene assets) |
| `eBackup.App` | WinUI 3 GUI |
| `eBackup.Cli` | Command-line interface |
| `eBackup.Tests` | Tests |

Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (in Russian).

## Roadmap

- **Installer** as a single exe (self-contained — no .NET required on the user's machine) — next up
- **v0.1 release** on GitHub Releases
- The final logo (being drawn by an artist; the current one is temporary)
- Dynamic DLL plugins (with a trust model and signatures)
- Windows registry key backup; more application modules
- English UI; Windows Task Scheduler integration
- Selective reads of **encrypted** archives without a full download

## License
[MIT](LICENSE) © Erney (erneywhite)
