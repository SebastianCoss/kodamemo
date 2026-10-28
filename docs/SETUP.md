# Local setup

## Requirements

- Web/tests: .NET 9 SDK and runtime.
- Android: the current project targets .NET 11 preview, the corresponding MAUI Android workload, Android SDK and JDK. Preview workload compatibility must be checked for your machine.
- A Whisper model compatible with Whisper.net 1.9.1. Model weights are not bundled; check their license and origin.

Build individual projects rather than the complete solution if Android workloads are unavailable.

## Web configuration

Use .NET user secrets for local development. Replace placeholders with independently generated, strong values. Do not commit the resulting values.

```sh
dotnet user-secrets set "KodaMemo:WebUsername" "owner" --project src/KodaMemo.Web
dotnet user-secrets set "KodaMemo:WebPassword" "<your-password>" --project src/KodaMemo.Web
dotnet user-secrets set "KodaMemo:SyncBearerToken" "<random-sync-token>" --project src/KodaMemo.Web
dotnet run --project src/KodaMemo.Web --environment Development --urls http://localhost:5188
```

Open http://localhost:5188 and sign in. User secrets are a development convenience, not an encrypted production secret store. Environment variables use double underscores, for example KodaMemo__SyncBearerToken.

The application refuses access when required settings are missing. The first installation contains no meetings.

## Android connection

Build the Android project with the matching SDK/workloads. Configure the server URL and sync token in the app settings. The token is stored using platform SecureStorage.

HTTPS is required except for localhost/127.0.0.1 development connections. When testing over USB, use Android platform tools to reverse port 5188 and configure the app with http://localhost:5188. A phone's localhost normally refers to the phone itself.

Do not commit a debug or release signing key. Keep your own release signing material outside this repository.

## Data and backups

Web data lives under the web project's Data directory. Android stores recordings and its database in application storage. Both are excluded from Git. Source-control synchronization does not transfer your meetings.

Copying a database while it is being written can produce an inconsistent backup. Stop the application before manual backups. Keep recordings and secrets out of public issue attachments.
