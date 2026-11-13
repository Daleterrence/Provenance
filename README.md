# Provenance

Provenance is a launcher for FINAL FANTASY XI that replaces the PlayOnline Viewer, speeding up game login, patching and improving the friends and messaging systems.

- Saves your account details securely, logs in with one click, and provides integration for your one-time password from Bitwarden, 1Password or KeePassXC
- Works alongside Windower 4 and Ashita v4
- Your friend list, messages and profile, without opening POL
- Installs, patches and repairs the game for you, speeding up the process on the whole.
- Multi-account mode for multiboxers, making log in across many accounts easier than ever.

## Installation

Download the Setup.exe file from the latest release on the [releases](https://github.com/Daleterrence/Provenance/releases) page (it's Provenance-beta-Setup.exe during the beta) and run it. The app will keep itself up to date for you after that!

## Build

You'll need the .NET 10 SDK and Visual Studio Build Tools with the x86 C++ compiler. Provenance's images aren't in
this repo, so you'll also need an `assets` folder next to it (or `PROVENANCE_ASSETS` set to one) with
`app\provenance.ico`, `app\provenance-256.png` and `app\bg-logo.png` in it, plus any installer pictures as `.jpg`
files in `installer\zones`. Then:

```
pwsh native/build-native.ps1
dotnet build Provenance.slnx
```

## Need help?

Have a look at the [FAQ](https://provenancexi.app/faq).

## Licence

Provenance is licensed under the GNU General Public License v3.0, see [LICENSE](LICENSE). The open source software it
uses, and their licences, are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Provenance is not affiliated with or endorsed by SQUARE ENIX CO., LTD, the Windower, or Ashita projects.

FINAL FANTASY, PlayOnline Viewer and FINAL FANTASY XI are registered trademarks of SQUARE ENIX CO., LTD.
