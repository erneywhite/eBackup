# eBackup

> 🌐 **Languages:** [Русский](README.md) · **English** (this file)

**eBackup** is a modular Windows backup app: it backs up application settings and
any folders you pick, stores archives in several places at once (local, SFTP),
and restores everything back into place — even on another PC or after an OS
reinstall. Branded dark UI plus a full CLI.

Free, no subscriptions — built for people.

> 🚧 Under active development, but the **full cycle already works**:
> backup → storage → restore → delete, from both the GUI and the CLI.

## Features

**GUI (WinUI 3, dark theme):**
- 🚀 **Backup** — pick modules (OBS Studio) and **any custom folders**, several
  destinations at once (local + servers), optional encryption, live progress
- 🌐 **Storage** — SFTP connections: the private key is pasted **as content**
  and stored encrypted (the key file doesn't have to stay on disk), a Test
  button, automatic availability badges (✓/✕ per connection), remote folder
  picking via a lazy-loading **tree**
- 📦 **Archives** — local and server listings (size, date, 🔒 for encrypted),
  one-click restore and delete
- ♻️ **Restore** — to the original locations **or to any folder you choose**;
  conflict modes (replace with `.bak` / overwrite / add missing only);
  external OBS scene assets land in a folder you pick and scene paths are
  rewritten automatically
- ⚙️ **Settings** — local backup folder and **keep-last-N retention**
  (old archives are pruned automatically, locally and on servers)

**CLI** — everything scriptable: `backup`, `restore`, `sftp-add/list/test/ls`,
`module-add`; pass the passphrase via the `EBACKUP_PASSPHRASE` environment
variable for automation.

**Modules:**
- 🎬 **OBS Studio** (built-in): configuration without caches/logs, plugins from
  the install folder, and **dependent scene assets** (background images, videos
  living outside OBS) — with path rewriting on restore
- 🧩 **Declarative drop-in modules**: put a `*.module.json` into
  `%APPDATA%\eBackup\modules` (or `ebackup module-add file`) and the app starts
  backing up the described paths. No code, no rebuild.

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

**Security:**
- 🔐 Optional archive encryption: **AES-256-GCM** with the key derived from a
  passphrase via **Argon2id** — safe to store on third-party servers
- 🔑 Connection secrets (passwords, keys) go through **Windows DPAPI**, never
  sit in the config as plaintext, and intentionally **don't roam** to other PCs
- 🛡️ Declarative modules are restricted to app-data paths (no `.ssh`/keys/
  Program Files); archives are validated against path traversal

**The `.ebk` format** — ZIP + `manifest.json` with tokenized paths
(`{APPDATA}` etc.), so archives are **portable between machines**. Names are
self-describing and sortable: `ebackup_obs-folders_2026-06-10_01-55-27.ebk`.

## Build & run

Requires [.NET SDK 9.0+](https://dotnet.microsoft.com/download) (Windows 10/11).

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
| `eBackup.Abstractions` | Plugin contract (interfaces, manifest model) — the stable boundary |
| `eBackup.Core` | Backup/restore engine, encryption, module registry, declarative modules |
| `eBackup.Security` | Secret protection via Windows DPAPI |
| `eBackup.Storage.Local` / `.Sftp` | Storage backends: local folder and SFTP (SSH.NET) |
| `eBackup.Modules.Obs` | OBS Studio module (config + plugins + scene assets) |
| `eBackup.App` | WinUI 3 GUI |
| `eBackup.Cli` | Command-line interface |
| `eBackup.Tests` | Tests |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (Russian) for details.

## Roadmap
- Modules screen in the GUI (registry cards, import)
- Live dashboard on the Overview page
- Tray icon and scheduled automatic backups
- Windows registry key backup; more application modules
- FTP and Google Drive storage backends
- Dynamic DLL plugins (with a trust model and signing)
- Single-exe installer; UI animations; logo

## License
[MIT](LICENSE) © Erney (erneywhite)
