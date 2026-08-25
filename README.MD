# LSP (Local Stream Player)

LSP is a Windows desktop application for organizing and playing locally stored movies and TV shows. It scans folders selected by the user, builds a local library, plays media through a local web server, and can optionally enrich the library with metadata from TMDB and an LLM provider through OpenRouter.

LSP is intended only for content that the user is legally allowed to access. It does not download movies or TV shows and is not intended to support copyright infringement or software piracy.

## Features

- Local movie and TV-show library
- Audio-track and subtitle selection
- Direct playback and FFmpeg-based transcoding
- Optional TMDB metadata and artwork
- Optional OpenRouter-assisted filename matching
- Portable export and import for selected media

## Requirements

- Windows x64
- [.NET SDK 10.0.101](global.json) or a compatible newer feature release
- Node.js and npm
- `ffmpeg` and `ffprobe` in `PATH`, or under `tools/ffmpeg/<runtime>`

This repository contains source code only. Prebuilt executables and the Windows installer packaging files are not included.

## Run from source

Build the web frontend first:

```powershell
Set-Location src/web
npm ci
npm run build
Set-Location ../..
```

Then run the desktop application:

```powershell
dotnet run --project src/LSP.App/LSP.App.csproj
```

During development, `./run-dev.ps1` builds and starts a fresh local instance.

## Security and privacy

LSP is designed as a single-user, local desktop application. It is not designed to be exposed as a network service or used across an untrusted network.

### Local server has no authentication

By default, the embedded HTTP server listens on a random port on `127.0.0.1`. Its API has no authentication, authorization, or TLS and includes operations that can modify settings, clear library data, import a database, export or move media, and control playback/transcoding.

Do not bind LSP to a non-loopback address and do not expose its port to a LAN, the internet, a reverse proxy, or an untrusted local user. In particular, do not set `LSP_LISTEN_URL` or use `--listen` with addresses such as `0.0.0.0`, a LAN IP, or a public interface unless you have added an appropriate security layer yourself.

### API keys are stored unencrypted

TMDB and OpenRouter API keys entered in Settings are stored as plain text in the local SQLite database. They are not protected with DPAPI, Windows Credential Manager, or application-level encryption.

Treat the following as sensitive:

- `%LOCALAPPDATA%\LSP\library.db` in an installed build
- `data/library.db` inside a portable package
- database backups created during import
- any copy or backup of the LSP data directory

Use restricted keys and provider-side spending limits where available. Revoke and replace a key if a database, backup, or portable package containing it is lost or shared.

### Portable export and import can be destructive

Portable exports contain selected media, library metadata, playback progress, cached metadata, posters when enabled, and a database snapshot. The current database snapshot also contains stored application settings, including configured API keys. A portable package must therefore be handled as confidential data and must not be published or shared without inspecting and sanitizing it first.

The **Move** export mode deletes the original media files after the exported package and its database have been successfully published. Keep an independent backup and verify the destination before using this mode.

Import replaces the active local library database and poster directory with data from the selected package. LSP creates local backups as part of the import process, but these are not a substitute for an independent backup. Import packages only from trusted sources.

### External services receive metadata

TMDB receives search terms and media identifiers needed for metadata enrichment.

When OpenRouter-assisted matching is enabled, LSP sends media filenames, folder names, parsed titles, years, candidate metadata, and limited candidate overviews to the configured model through OpenRouter. File contents and video streams are not sent to OpenRouter by this feature, but filenames and folder names can themselves contain private information.

Do not enable LLM-assisted matching for libraries whose names or directory structure contain information you are not willing to disclose to the configured provider.

### Logs may contain private media information

LSP writes diagnostic logs under `%LOCALAPPDATA%\LSP\logs` in an installed build and `data/logs` in portable mode. LLM logs contain prompts and responses, which may include filenames, folder names, titles, and provider error messages.

Review and redact logs before attaching them to an issue or sharing them with another person. Logs and the complete application data directory should not be committed to source control.

## Third-party services

This product uses the TMDB API but is not endorsed or certified by TMDB. Use of TMDB and OpenRouter is subject to their respective terms and privacy policies.

## License

LSP is licensed under the [MIT License](LICENSE).
