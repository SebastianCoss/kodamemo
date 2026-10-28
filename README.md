# KodaMemo

Record conversations. Keep the useful parts.

KodaMemo is an early Android meeting recorder with a companion Blazor workspace for transcripts, agreements, tasks and mind maps. Koda is the assistant's name.

**Status: alpha, single-owner, local development.** This is not a hosted service or a production multi-user application.

[Documentación en español](README.es.md) · [Setup](docs/SETUP.md) · [Architecture and limitations](docs/ARCHITECTURE.md)

## Current scope

- Android microphone recording, pause/resume, local playback and interrupted WAV recovery.
- Local speech-to-text using Whisper.net; model files are supplied separately.
- SQLite storage and transcript versioning.
- Web meeting views, editable transcripts, checklists and maps generated from existing structured results.
- Token-authenticated synchronization and cookie-authenticated web access.

## In progress

The web recorder is a clearly labeled simulation. The current assistant uses application rules; it is not a general conversational model. Paid provider execution, reliable billing enforcement, voice interaction and public deployment remain unfinished. Existing chat subscriptions do not automatically include API access.

## Development

The web and test projects target .NET 9. Android currently targets .NET 11 preview with MAUI and Android API 26 or newer. These inherited targets need a supported stable SDK migration before a stable release.

See [setup](docs/SETUP.md) for configuration. No recordings, databases, model weights, API keys or signing keys are included.

```sh
dotnet test tests/KodaMemo.Tests/KodaMemo.Tests.csproj
```

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md). Bug reports with reproducible steps and small focused pull requests are welcome. Please use synthetic audio and transcripts in public issues.

## License

Project code is MIT licensed. Dependencies and bundled fonts retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
