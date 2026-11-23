# Provenance

Provenance is a launcher for FINAL FANTASY XI that replaces the PlayOnline Viewer. It speeds up logging in and patching, and improves the friends and messaging systems.

- Saves your account details securely, logs in with one click, and can get your one-time password from Bitwarden, 1Password or KeePassXC
- Works alongside Windower 4 and Ashita v4
- Your friend list, messages and profile, without opening the PlayOnline Viewer
- Installs, patches and repairs the game for you, and speeds the whole process up
- Multi-account mode for multiboxers, making logging in across many accounts easier than ever

## Installation

Download `Provenance-setup.exe` from [provenancexi.app](https://provenancexi.app) or from the latest release on the [releases](https://github.com/Daleterrence/Provenance/releases) page, and run it. The app will keep itself up to date for you after that!

You'll need Windows 10 or 11.

## Need help?

Have a look at the [FAQ](https://provenancexi.app/faq), or join the [Discord server](https://discord.gg/pFtdzeJ6p4).

Found a bug? Open an [issue](https://github.com/Daleterrence/Provenance/issues).

## Building it yourself

You'll need the .NET 10 SDK, PowerShell 7, and the Visual Studio Build Tools with the C++ x86/x64 tools.

1. Run `pwsh native/build-native.ps1`. It builds the native parts, and downloads Provenance's images the first time (about 7 MB), as they aren't in this repository.
2. Run `dotnet build src/Provenance.Wpf`.

The app is 32-bit, so running your build also needs the x86 .NET 10 Desktop Runtime.

## Licence

Provenance is licensed under the GNU General Public License v3.0, see [LICENSE](LICENSE). The open source software it
uses, and their licences, are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Image credits are in [CREDITS.md](CREDITS.md).

Provenance is not affiliated with or endorsed by SQUARE ENIX CO., LTD., or the Windower or Ashita projects.

FINAL FANTASY and SQUARE ENIX are registered trademarks of Square Enix Holdings Co., Ltd. PLAYONLINE is a registered
trademark of Square Enix Co., Ltd.
