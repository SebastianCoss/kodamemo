# Continue on another computer

## Clone the source

```sh
git clone https://github.com/SebastianCoss/kodamemo.git
cd kodamemo
```

The repository contains source code. It does not contain recordings, your meeting database, model files, development passwords or Android signing keys.

## Install the development tools

The current shared library targets both net9.0 and net11.0. Use the matching .NET 11 preview SDK to restore the current solution, even when running only the web tests, and install the .NET 9 runtime to run the web application. Check dotnet --list-sdks and dotnet --list-runtimes.

Android additionally requires the matching MAUI Android workload, Android SDK and JDK. The project has not yet been migrated to a stable SDK baseline.

## Run checks first

```sh
dotnet test tests/KodaMemo.Tests/KodaMemo.Tests.csproj
```

This checks shared/web behavior and WAV recovery; it does not validate microphone access, Android lifecycle or device installation.

## Configure the web application

Follow [SETUP.md](SETUP.md) to create local credentials and start the server. Generate fresh credentials on this computer. Do not copy secret values into Git or issue descriptions.

When accessing a server from a physical phone, localhost refers to the phone. Use a development USB port reverse or a properly configured HTTPS endpoint; see the setup guide.

## Continue development

Create a branch for each focused change:

```sh
git switch -c feature/your-change
```

Run the relevant checks, commit with your own Git identity, push the branch and open a pull request.

A ChatGPT subscription does not automatically configure the developer environment or grant API access to the app. After cloning, open the repository in your editor and provide any local configuration separately.
