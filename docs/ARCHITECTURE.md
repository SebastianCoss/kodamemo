# Architecture and release limits

## Projects

- KodaMemo: .NET MAUI Android UI, microphone foreground service, PCM/WAV recording, playback, SQLite and local Whisper transcription.
- KodaMemo.Shared: meeting contracts, transcript/evidence states, map construction and comparison rules.
- KodaMemo.Web: Blazor Server UI, SQLite-backed single-owner store, local login and synchronization endpoints.
- KodaMemo.Tests: MSTest checks for evidence semantics, persistence, required configuration and WAV recovery.

Audio capture and speech recognition remain on Android. Web synchronization exchanges meeting data; source control is not a meeting backup. Review the exact synchronization payload before using confidential recordings.

Evidence matching and user confirmation are distinct states. Finding a quotation proves its presence in the transcript, not the correctness of an inferred task or agreement.

## Security boundaries

Web pages require a login cookie; sync APIs require an independently configured bearer token. Login/logout validate antiforgery tokens. Audio downloads require authentication and are restricted to the server's audio directory. Android does not permit arbitrary cleartext remote servers.

This is a single-owner alpha. It has no per-user tenant isolation, production identity lifecycle or independent security audit. SQLite and WAV data are not encrypted at rest by this application. Protect the device, host and backups.

## Known gaps

- Web recording is simulated.
- Assistant replies are rule-based and paid summarization providers are not complete.
- Budget displays and reservation code must not be treated as a provider-side spending cap.
- SDK targets need migration before a supported stable release.
- Long recordings, interruptions and synchronization conflicts need broader physical-device testing.
- The publication copy has not been installed and verified on a physical phone.
- Dependency vulnerability verification must complete successfully before a release; a failed NuGet audit is not a clean audit.

## Roadmap

1. Finish reproducible local setup, device validation and SDK migration.
2. Replace simulated web recording with real permission-aware browser capture.
3. Evaluate summarization providers using synthetic Spanish meeting transcripts, recording cost and quality.
4. Add reviewed exports/imports, voice interaction and stronger synchronization conflict handling.
