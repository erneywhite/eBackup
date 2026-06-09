# eBackup

> 🌐 **Languages:** [Русский](README.md) · **English** (this file)

**eBackup** is a modular Windows backup application. It backs up important data
and application settings, stores them in several destinations at once (local,
network share, FTP/SFTP, cloud), and restores everything back to the right
places — even on a different machine or after an OS reinstall.

The first module is a **full OBS Studio backup** (scenes, profiles, settings,
connections).

> ⚠️ Early development (v1, vertical slice). APIs and formats may change.

## Why
Moving to a new PC or reinstalling Windows makes it easy to lose app settings
collected over years. eBackup bundles them into a single portable `.ebk` archive
with a **path manifest**, so on restore files land exactly where they belong —
even if the username, drive letter, or install path changed.

## Features
**Done in v1 (vertical slice):**
- 📦 `.ebk` archive format (ZIP + `manifest.json` with tokenized paths)
- 🧩 Module system: declarative descriptor + optional code hook
- 🎬 **OBS Studio** module (`%APPDATA%\obs-studio`)
- 💾 Storage: local / network folder
- ♻️ Manifest-driven restore with a conflict-resolution policy
- 🖥️ CLI for running and debugging

**Planned:**
- 🔐 Optional archive encryption (AES-256-GCM, key derived from a passphrase)
- 🌐 Storage backends: SFTP, FTP, Google Drive and more — several at once
- 🪟 WinUI 3 GUI with a tray icon
- ⏰ Scheduling via Windows Task Scheduler, versioning and retention
- 🗂️ Registry-key backup, more application modules

## Architecture

| Project | Purpose |
|---|---|
| `eBackup.Core` | Core: manifest model, backup/restore engine, archive format, `IStorageProvider` and `IBackupModule` abstractions |
| `eBackup.Storage.Local` | Local/network folder storage |
| `eBackup.Storage.Sftp` | SFTP storage *(stub, SSH.NET implementation pending)* |
| `eBackup.Modules.Obs` | OBS Studio backup module |
| `eBackup.Cli` | Command-line interface |
| `eBackup.Tests` | Tests |
| `eBackup.App` *(later)* | WinUI 3 GUI |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (Russian) for details.

## Build & run

Requires [.NET SDK 9.0+](https://dotnet.microsoft.com/download).

```powershell
dotnet build eBackup.sln
dotnet test  eBackup.sln
```

CLI:

```powershell
# list available modules
dotnet run --project src/eBackup.Cli -- list-modules

# back up all modules into .\backups
dotnet run --project src/eBackup.Cli -- backup --out .\backups

# restore from an archive
dotnet run --project src/eBackup.Cli -- restore --archive .\backups\ebackup-20260609-120000.ebk
```

## License
[MIT](LICENSE) © Erney (erneywhite)
