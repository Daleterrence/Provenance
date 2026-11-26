# How logging in works

I've written out this document based on some requests from people to explain exactly how Provenance works in relation to your login info, and overall security of the app itself. I completely understand the apprehension about it, and especially for your details, it's entirely fair to want to know exactly what happens to them after they are input into Provenance. This page explains it, first in plain terms and then step by step, with links to the exact code that does each part, so you don't have to take my word for it.

To give you a TL;DR version - Your login details only ever go to Square Enix, and they're sent by PlayOnline's own code, the same code that sends them when you log in with the PlayOnline Viewer. Provenance doesn't send them anywhere else.

---

For those of you that prefer the detailed version, I'll start by saying that you don't need to be able to read code to follow this page. Each section starts with what it means for you, and the
technical detail and links come after that, for anyone who wants to verify the claims made.

Everything here describes `v1.0.0-pb.2.2`, and every link points at that release's code, so the line numbers won't drift as the code changes.

## The key bits

- Your details are **only** kept on your PC, in Windows Credential Manager (Windows' own password store), encrypted with Windows' own data protection and an extra key that only Administrators and SYSTEM can read.
- PlayOnline is what actually sends them. Provenance starts PlayOnline's own program, `pol.exe`, and hands your details to PlayOnline's own login code inside it, which sends them to Square Enix just like it would from the viewer's login screen.
- Your PlayOnline and Square Enix IDs and passwords never go into a file, a command line or an environment variable along the way. Provenance only tells PlayOnline *where* in Credential Manager to look. A few masked or hashed traces do end up on disk, and I've listed every one of them [further down](#what-else-is-kept-on-your-pc).
- None of Provenance's own internet traffic (updates, news, Provenance's patch server, the lot) includes your PlayOnline or Square Enix IDs, your passwords or your one-time passwords. Every server it talks to is [listed below](#everything-else-provenance-connects-to).
- Provenance never writes passwords or one-time passwords to its logs, and IDs are masked.
- Provenance is built by GitHub from the public code, and every update is signed by me and checked by Provenance before it installs. There's more on that in [how Provenance gets from GitHub to your PC](#how-provenance-gets-from-github-to-your-pc).

## Explanation of terms you'll see through this document

- `pol.exe` is the PlayOnline Viewer's main program, from your PlayOnline install.
- `polcore.dll` is the part of PlayOnline that talks to Square Enix's servers. It's Square Enix's own file, also from your PlayOnline install.
- The hook (`pol_hook.dll`) is a small part of Provenance that runs inside `pol.exe`. It tells PlayOnline's code what to do, the same way the viewer's own screens normally would ([`native/hook/`][hookdir]).
- Provenance Watcher (`Provenance Watcher.exe`) is a small program that starts `pol.exe` and loads the hook into it ([`native/watcher.c`][watcher]).
- Provenance itself (`Provenance.exe`) is the app you actually see. Its code is in [`src/`][src].
- Windows Credential Manager is Windows' built-in password store, and you can open it from Control Panel if you fancy a look.

## 1. Saving your details

When you save an account, your details get encrypted and stored in Windows Credential Manager on your PC, and that's it. Saving them doesn't send them anywhere. The only time they ever leave your PC is when you log in, which is what the rest of this page is about.

Provenance saves two entries, one with your PlayOnline ID and password, and one with your Square Enix ID and password ([`AccountsViewModel.cs`][save-vm], [`AccountsViewModel.cs`][save-vm-edit], [`CredentialService.cs`][credsvc-save]). They're generic credentials named `Provenance:POL:<account>` and `Provenance:SEA:<account>`, where `<account>` is Provenance's own ID for that account ([`CredentialService.cs`][credsvc-key], [`CredentialTargets.cs`][credtargets], [`WindowsCredentialStore.cs`][wcs-format]).

Before anything goes in an entry, it gets wrapped up first ([`WindowsCredentialStore.Save`][wcs-save]):

1. The ID and password are put together as UTF-16 text: the ID, a null character, then the password ([`Pack`][wcs-pack]).
2. That's encrypted with Windows' Data Protection API, `CryptProtectData`, which ties it to your Windows account ([`Dpapi.cs`][dpapi]).
3. The encryption also takes some extra "entropy", which in this case is a 32-byte key followed by the entry's own name ([`WrapEntropy`][wcs-entropy]). Without that key, the entry can't be decrypted, and because the name's part of it too, an entry copied to a different name can't be either.
4. The result is saved with `PVW2` in front of it, and the entry's own user name field is left blank ([`WindowsCredentialStore.cs`][wcs-blank], [`Write`][wcs-write]), so there's nothing readable sat in Credential Manager.

The key itself is 32 random bytes in a file called `cred.key` ([`CredentialKey.cs`][credkey-load], [`CredentialKey.cs`][credkey-create]), kept in `C:\ProgramData\Provenance\ipc` ([`CredentialKey.cs`][credkey-path], [`ProtectedFolders.cs`][protfolders], [`AppPaths.cs`][apppaths]). Provenance locks that folder down so only Administrators and SYSTEM can open it ([`LockedFolder.cs`][locked]). Provenance runs as administrator ([`app.manifest`][manifest]), which is how it can read the key. Any program running as you *without* administrator rights can only ever get the encrypted blob out of Credential Manager. A program that *does* have administrator rights, or runs as SYSTEM, could read the key, so as always, be careful what you give admin rights to.

When the account dialog checks your details (the "Verifying your account information..." bit), that's a real login, and it goes exactly the same way as everything below ([`AccountCheck.cs`][acct-check]).

## 2. Pressing Play

When you press Play, Provenance doesn't just hand your details over to PlayOnline. Instead, it tells PlayOnline where to find them in Credential Manager, and the hook inside PlayOnline goes and reads them itself.

First, Provenance reads the two entries back and decrypts them ([`AccountRules.PrepareLogIn`][prepare]), then hands them over to the login ([`GameLaunch.LogInAsync`][gamelaunch], [`PolSession.ConnectAsync`][connect]).

Before it starts `pol.exe`, it does two checks that don't involve your details at all:

- It makes sure the viewer's up to date with Square Enix's patch server. The viewer itself does the same before it logs in, every time you connect ([`PolSession.cs`][viewer-update]).
- It makes sure `pol.exe` and `polcore.dll` are builds the hook knows, and keeps them locked until the login's done ([`PolSession.cs`][hold], [`SignedBuilds.cs`][signedbuilds]). If they're a build it doesn't recognise yet, it fetches a signed list of builds from Provenance's patch server first.

Then it gets the entries ready for the hook ([`LaunchCredentials.TargetsFor`][targets]):

- If the account's saved entries match the IDs being used, the hook's pointed straight at them.
- If they don't (say, details you've typed into the account dialog but haven't saved yet), Provenance writes one-shot copies named `Provenance:LAUNCH:<launch>:POL` and `Provenance:LAUNCH:<launch>:SEA`, wrapped in exactly the same way ([`SaveOneShot`][oneshot-save]). One-shot entries only last until you log out of Windows ([`WindowsCredentialStore.cs`][wcs-persist]).
- A one-time password, if you use one, always gets a one-shot entry of its own, `Provenance:LAUNCH:<launch>:OTP`.

`pol.exe` is told where to find them through its environment ([`PolSession.cs`][buildenv], [`HookEnvironment.SetLogin`][setlogin], [`HookEnvironment.SetBase`][setbase]). That environment holds the entries' *names* (`POL_CRED_POL`, `POL_CRED_SEA`, `POL_CRED_OTP` and so on), the key file's path and a few launch settings, like which character to pick. It **never** holds your PlayOnline ID, your Square Enix ID, your one-time password or any password.

Provenance Watcher then starts `pol.exe` suspended, loads `pol_hook.dll` into it, and lets it run ([`Watcher.LauncherStart`][launcherstart], [`watcher.c`][watcher-main], [`start_suspended`][watcher-start], [`hook_and_resume`][watcher-resume]). `pol.exe` gets the watcher's environment as it is.

There are a few variations on this, but they all end up in the same place:

- To save you some time, Provenance can start `pol.exe` before you even press Play. The hook just sits and waits, and apart from the early read-only connection in section 4, it doesn't send anything whilst it does ([`login_prestart_wait`][prestart]). When you log in, Provenance writes the same entry names to a file in an administrators-only folder ([`HookEnvironment.WriteGoFile`][gofile], [`App.xaml.cs`][hookdir-set]), and the hook only takes the settings it's expecting from that file ([`login.c`][prestart-allowed]).
- With Windower, Provenance Watcher starts `pol.exe` with both Windower's hook and Provenance's loaded ([`watcher.c`][watcher-windower], [`WindowerStart.cs`][windower-start]).
- With Ashita, Ashita starts `pol.exe` itself, and Provenance Watcher loads the hook into it afterwards ([`AshitaLauncher.cs`][ashita-apply], [`HookEnvironment.ApplyContract`][applycontract], [`watcher.c`][watcher-attach]).

Saying that, if you use Windower or Ashita, their addons and plugins run inside the same `pol.exe` as the hook. Provenance has no control over those, naturally, so only use ones you trust.

## 3. Inside pol.exe

This is the bit where your details actually get sent, and the important part is that it's **PlayOnline's own code** doing the sending, not mine. The hook gives PlayOnline your details the same way the viewer's login screen would, and PlayOnline does the rest.

When the hook loads ([`DllMain`][dllmain]), it changes the point in `pol.exe` where the viewer would open its own menu and login screen, so it goes to the hook instead ([`pol_hook_install`][install], [`pol_hook_menu_stub`][menustub]). (That's why you never see the viewer's login screen when you use Provenance.) From there, the hook logs in using `polcore.dll`'s own functions ([`login.c`][fakesession], [`log_in`][login]).

First, it reads your details ([`cred_read_details`][readdetails], [`pc_cred_read`][pccredread]):

- It reads the entry with `CredReadW`, checks it starts with `PVW2`, reads the 32-byte key file and decrypts the entry with `CryptUnprotectData`, using the same key-plus-name entropy as before ([`pc_read_key`][readkey], [`pc_entropy`][entropy]).
- An entry that *isn't* wrapped gets refused, so another program can't plant a plain entry for the hook to send on. The entry's own user name field is never even read ([`pol_cred.h`][pvw2], [`credentials.c`][plain-refused]).
- One-shot entries are deleted the moment they've been read ([`cred_consume`][consume]).
- If anything's wrong with an entry, or the PlayOnline password is longer than the 16 characters PlayOnline allows, `pol.exe` closes before the login even starts ([`cred_read_details`][readdetails], [`log_in`][pwlen]).

Then, it hands them to `polcore.dll` ([`login_session`][session-steps]):

1. `polcore.dll` sets up its connection (session init, inet init, `POLCON_OPEN`).
2. Your PlayOnline password goes through polcore's own password encoder, and is set with polcore's own setter. The hook's plain copy is wiped straight away.
3. The address polcore logs in through is set to `ci000.pol.com`, using polcore's own host encoder and setter. That's the viewer's own default: it encodes `ci000.pol.com` with the same encoder for each new member it sets up, then gives that to the same setter when you log in.
4. Your PlayOnline ID goes through polcore's own ID encoder, and the hook's copy is wiped.
5. Your Square Enix password is turned into the value polcore expects, `SHA-1(hex(SHA-1(password)) + "playonline")`, worked out with polcore's own SHA-1 functions, the same way the viewer does it. That, and your Square Enix ID, go to polcore's own Square Enix account setter, along with your one-time password if you have one (a code that isn't six digits is left out), and every copy the hook had is wiped ([`seed_account_login`][seed], [`cred_sha1`][sha1], [`credentials.c`][otp-six]).
6. The hook starts polcore's login, and keeps polcore's own connection loop (`POLCON_STEP` and `POLCON_IDLE`) ticking over until it finishes ([`login_auth_run`][authrun]). If Square Enix doesn't accept the login, `pol.exe` closes.

Every connection that carries your details is made by `polcore.dll`. The functions the hook calls are all polcore's own, found through polcore's service table or by matching their bytes in `polcore.dll` ([`polcore.h`][svc], [`polcore.h`][svc2], [`addresses.c`][addresses]). The hook doesn't have its own version of any of them.

## 4. The two changes to polcore's connections

This one sounds scarier than it is, so bear with me. The hook makes logging in a little bit quicker by changing how PlayOnline's connections start. Neither change sends anything of yours anywhere, and here's what they actually do.

The hook swaps polcore's `connect` for its own ([`polcore_sockets.c`][sockets], [`polcore_sockets_hook`][sockets-hook]), which turns on `TCP_NODELAY` for every polcore connection (polcore never turns it on, so the body of each request sits waiting for the server to acknowledge its header first), and then connects to the address polcore asked for, apart from one connection ([`polcore_connect`][polcore-connect]):

- polcore's login starts by connecting to `ci000.pol.com` on port 51240 and reading its first line, which tells polcore which login server to connect to next. polcore sends nothing before that line arrives ([`redirector.c`][red-host]).
- When a login's on its way, the hook makes that connection early, whilst the rest of `pol.exe` is still starting up ([`redirector_start`][red-start], [`DllMain`][dllmain-red]). It *only* reads what the server sends, and never sends a thing ([`redirector_ask`][red-ask]).
- When polcore then connects to port 51240, it's connected to a listener on `127.0.0.1` inside `pol.exe` instead, which gives polcore that same answer ([`redirector_aimed`][red-aimed], [`redirector_connect`][red-connect], [`redirector_listen`][red-listen]). The listener only answers a connection from `127.0.0.1` on the exact port polcore's own socket has ([`redirector_serve`][red-serve]).
- If the early answer isn't there, it's more than a minute old, or anything at all goes wrong, polcore just makes the connection itself like it normally would.

Provenance also looks up `ci000.pol.com` ahead of time so the address is ready to go ([`Prestarter.ResolveDns`][dns]). That's a DNS lookup, nothing more.

Those are the only sockets the hook ever opens itself, and there's no other network code in it.

## 5. After logging in

Once you're logged in, the temporary copies get cleaned up, and your details aren't touched again until the next time you log in.

- Provenance deletes the launch's one-shot entries as soon as the login's over, whether it worked or not ([`PolSession.cs`][cleanup]).
- Any one-shot entries older than 10 minutes are swept up when Provenance starts, and every 10 minutes after that ([`LaunchCredentials.cs`][sweep-start], [`LaunchCredentials.cs`][sweep]). So if a crash ever leaves one behind, it's gone the next time Provenance starts (or when you log out of Windows), and it stays encrypted in the meantime.
- Friends, messages and everything else use the session polcore already has. The friend zone lookup is the one exception, and it's in [the table below](#everything-else-provenance-connects-to).
- A chat room password you type reaches the hook the same way your details do, through a one-shot entry that's deleted as soon as it's read ([`LaunchCredentials.SaveChatPassword`][chat-save], [`chat_room.c`][chat-read]).

## One-time passwords from a password manager

If you'd rather not type your one-time password in every time, Provenance can get it from 1Password, Bitwarden or KeePassXC instead ([`OtpSources.FetchAsync`][otp-fetch]). Whichever one you use, it only ever fetches the six-digit code. None of them are given your PlayOnline or Square Enix details, and the code goes into the one-shot entry from step 2.

- With 1Password and Bitwarden, Provenance runs its own copy of their command-line tool. It downloads that copy from 1Password or Bitwarden when you ask it to, and only ever runs that copy, never one installed somewhere else ([`ToolFinder.cs`][toolfinder]).
- With 1Password, Provenance runs `op item get <item> --otp`, with the item and account you've set ([`OnePassword.cs`][op]).
- With Bitwarden, Provenance runs `bw get totp <item>` ([`Bitwarden.cs`][bw]). Logging in to Bitwarden happens in Bitwarden's own window ([`Bitwarden.cs`][bw-login]). If Bitwarden's locked, you type your master password into Provenance's window, and it goes into the environment of that one `bw unlock` process, never onto a command line, and it isn't saved anywhere ([`OtpDialog.xaml.cs`][otpdialog], [`Bitwarden.cs`][bw-unlock]). Provenance keeps Bitwarden's session key in memory until it closes, then locks Bitwarden again ([`Bitwarden.cs`][bw-session], [`LockAtExitAsync`][bw-lock]).
- With KeePassXC, Provenance talks to it through KeePassXC's own `keepassxc-proxy.exe`, and will only use that from KeePassXC's Program Files folder ([`FindProxy`][kp-proxy]). It asks for the entries saved for the URL you've set ([`KeePassXc.FetchAsync`][kp-fetch], [`GetLoginsAsync`][kp-getlogins]). If more than one comes back, your Square Enix ID is used on your PC to pick the right one, and isn't sent to KeePassXC ([`PickEntry`][kp-pick]). The key Provenance pairs with KeePassXC is saved wrapped, the same way as your details ([`PairAsync`][kp-pair]).

(The tools themselves still talk to their own services as normal, that part's between you and them.)

## Logs

Logs are there so I can help you out when something goes wrong, and your passwords never go in them.

- In Provenance's own log, account details just show up as `***` ([`ICredentialService.cs`][redact-acct], [`LoginRequest.cs`][redact-req], [`WindowsCredentialStore.cs`][redact-stored], [`OtpSources.cs`][redact-otp]).
- The hook's log only ever shows the first two characters of an ID and how long it is, like `AB***(8)` ([`cred_mask_id`][mask]). Passwords and one-time passwords are never written to it.
- Logs stay on your PC. Provenance doesn't upload them anywhere.

## What else is kept on your PC

Apart from the Credential Manager entries, these are the only traces of your details Provenance keeps, and none of them can be turned back into a password at all:

- A new account's name starts out as the first two characters of your PlayOnline ID followed by asterisks, like `AB******`, until you rename it ([`AccountRules.MaskedId`][masked-name], [`AccountsViewModel.cs`][masked-use]).
- The hook's log, as mentioned above.
- A keyed hash of your PlayOnline ID, so Provenance can warn you before a wrong password could get your account locked. The key is `cred.key`, so the hash can't be matched to your ID without it ([`RefusedPasswords.cs`][refused]).
- For 2 minutes after a one-time password's been used, a keyed hash of it along with your Square Enix ID, so the same code isn't sent twice when accounts share an authenticator. Its key is kept in the same administrators-only folder ([`OtpReuse.cs`][otpreuse], [`OtpReuse.cs`][otpreuse-hash], [`AccountsViewModel.cs`][otpreuse-key]).

## Everything else Provenance connects to

This is every server Provenance connects to on its own, apart from the login itself. None of these connections carry your PlayOnline or Square Enix IDs, your passwords or your one-time passwords.

| Server | What for | Code |
|---|---|---|
| `pt008.pol.com` (`pt004.pol.com` for the Japanese version) and Square Enix's other `pol.com` patch servers | PlayOnline and FINAL FANTASY XI updates. Provenance makes these connections itself, and they only carry the product, its version and the names of the files it needs. Like the viewer, it takes the viewer's patch server from the viewer's own `env.dat`, and only falls back to `pt008.pol.com` or `pt004.pol.com` if that's missing or names something unexpected. | [`PatchProduct.cs`][patchproduct], [`PolUpdater.cs`][polupdater], [`PatchConnector.cs`][patchconnector], [`PatchProtocol.cs`][patchprotocol] |
| `gdl.square-enix.com` | The FINAL FANTASY XI installer download | [`GameInstaller.cs`][gameinstaller] |
| FINAL FANTASY XI's search servers | Finding which zone your friends are in. It sends your friends' character IDs. | [`SearchServers.cs`][search], [`SearchClient.cs`][searchclient] |
| `www.playonline.com` and its subdomains | News banner pictures | [`BannerImages.cs`][banners], [`PlayOnlineSite.cs`][playonline] |
| `patch.provenancexi.app` | Provenance's patch server, and the signed list of viewer builds | [`MirrorLayout.cs`][mirror], [`MirrorLayout.cs`][mirror-builds] |
| `updates.provenancexi.app` and GitHub releases | Provenance updates | [`SelfUpdate.cs`][selfupdate], [`ChannelSource.cs`][channel] |
| `news.provenancexi.app` | Provenance news | [`FeedService.cs`][feed] |
| `files.windower.net` and GitHub | Windower and its addons | [`WindowerHttp.cs`][windower] |
| `app-updates.agilebits.com`, `cache.agilebits.com`, `vault.bitwarden.com`, GitHub, and the certificate authorities that check the tools' signatures | Downloading the 1Password and Bitwarden command-line tools when you ask it to, then checking for a newer one in the background after it gets a one-time password from them | [`ToolInstaller.cs`][toolinstaller], [`ToolInstaller.cs`][toolinstaller-cdn], [`ToolInstaller.cs`][tool-update], [`ToolSignature.cs`][toolsig], [`OnePassword.cs`][op-update], [`Bitwarden.cs`][bw-update] |

## How Provenance gets from GitHub to your PC

So all of that is fantastic and great, but it only really means something if the Provenance on your PC is actually built from this code in the first place. For this release, which is `v1.0.0-pb.2.2`, it is: the installer and the update package were both built by GitHub from the public code in this repository, **not** on my own PC (the pictures come from outside it, and are checked as described below). Before an update goes out, I sign it, and your copy of Provenance refuses any update that isn't signed, or doesn't match its signature exactly. In short, this means that the code that goes into a build is the exact code that is available here.

Every public build of Provenance has gone out this way. (The older `beta` releases you'll see on the releases page are from the closed beta.)

### 1. GitHub builds it

When I tag a release, GitHub Actions builds it on GitHub's own machines, from exactly the code at that tag, using the [Build workflow][ci] in this repository ([`build.yml`][ci-pack]). Every run's log is public on the repository's Actions tab, for as long as GitHub keeps it.

- The build stops dead if the tag doesn't match the version in the code ([`build.yml`][ci-tagcheck]), or if any of the repository's files have changes that aren't committed ([`pack-release.ps1`][pack-clean]).
- It builds the hook, Provenance Watcher and the app from source ([`pack-release.ps1`][pack-build]), checks the native files going into the package (the hook, Provenance Watcher and the uninstallers) are the ones it's just built ([`pack-release.ps1`][pack-natives]), and packs the installer and the update package ([`pack-release.ps1`][pack-vpk]).
- Provenance's pictures aren't kept in this repository. The build downloads them, checks them against a SHA-256 fingerprint that *is* in the repository, and stops if they don't match ([`assets.json`][assets-pin], [`get-assets.ps1`][get-assets]).
- The finished files go on a draft release on GitHub ([`build.yml`][ci-draft]).

### 2. I sign it

This is the step that happens on my end. I download the update package GitHub built from the release, check the release's tag points at my commit, and sign a short statement about the package: that it's Provenance, its version and channel, its file name, its exact size and its SHA-256 fingerprint ([`AppRelease.cs`][apprelease]). The statement goes on the release next to the package, as `Provenance-<version>-<channel>-full.nupkg.sig`. Only the update package is signed, the installer isn't (more on that below).

The signing key isn't on GitHub, and nothing in the Build workflow signs anything, so a build on its own can never become an update Provenance will accept. The signing's done with my own tools, so that part isn't in this repository, but the checking is, and the checking is what decides what your copy accepts.

### 3. GitHub checks it and puts it on the update server

Once the release is published, the same workflow downloads the release's files from GitHub, runs the app's own check on the update package and its statement, and only uploads them to the update server (`updates.provenancexi.app`) if they pass. If the statement isn't there yet, nothing goes up ([`build.yml`][ci-check], [`ReleaseGate.cs`][releasegate], [`ReleaseGate.cs`][releasegate-asset]). The package and its statement go up exactly as they are on the GitHub release, nothing gets built again, and the only thing the upload adds to is the list of releases on the update server ([`build.yml`][ci-upload]). The installer on the website comes straight from a GitHub release's files too, but it isn't part of that check ([`build.yml`][ci-setup]).

### 4. Your copy checks it before installing

Every update Provenance downloads gets checked before it's installed ([`ChannelSource.cs`][channel-download], [`ChannelSource.cs`][channel-refuse]):

- The statement has to be signed by one of the two keys built into Provenance ([`MirrorKeys.cs`][keys]). These are ECDSA P-256 keys, and the signature is checked over SHA-256 ([`SignedBlob.cs`][signedblob]).
- It has to be for Provenance, for the version and channel being offered, and name the same file.
- The download has to be exactly the size the statement says, with exactly the same SHA-256 fingerprint ([`AppRelease.Check`][apprelease-check]).

If anything at all doesn't match, the download's deleted and the update doesn't happen. It's checked once when it downloads, and again just before it installs ([`SelfUpdate.cs`][selfupdate-check]). If the update server can't be reached, Provenance gets the same signed files from the GitHub release instead, and checks them the same way ([`ChannelSource.cs`][channel-fallback]).

Provenance installs updates by itself, and has to be up to date before it'll start ([`SelfUpdate.cs`][hastoupdate], [`SelfUpdate.cs`][update-flow]), so this check is what decides which updates reach your PC. Without the signing key, nobody can make an update your copy will accept, even with access to the update server or this repository (they could still stop new ones reaching you). And I only sign a package once I've checked the release's tag points at my commit.

A couple of things to be aware of, though:

- The installer is the one file the signature doesn't cover. (Sorry about this one, there's nothing on your PC to check it with until Provenance is there.) It's built by GitHub and comes straight from the release's files, and every update after you've installed is checked as above. If you'd rather, you can download `Provenance-setup.exe` from the release on GitHub instead of the website (see below for comparing the two).
- Provenance's files aren't signed with a Windows code-signing certificate, which is why Windows can warn you when you install, and with Smart App Control turned on, Windows can refuse to run them. The signature above is Provenance's own, and it's Provenance that checks it.

## Checking my work

None of this is required, of course, but if you'd like to double-check any of it, here's how.

- Every link on this page goes to the exact lines in the `v1.0.0-pb.2.2` source.
- To see which commit your copy was built from, find `Provenance.exe` in `%LocalAppData%\Provenance\current`, right-click it, then Properties > Details. The product version will read something like `1.0.0-pb.2.2+ed18c8b`, and the part after the `+` is the start of the commit it was built from ([`Directory.Build.props`][shortrev]). That's the commit the release's tag points at on GitHub. It's a label the build stamps in, so it's a useful way to match your copy to a release, but it isn't proof on its own.
- The website's installer is from the newest stable release, or the newest beta whilst there isn't a stable one yet. To check it's the same file as the one on GitHub, download `Provenance-setup.exe` from that release and from the website, then run `Get-FileHash .\Provenance-setup.exe` in PowerShell on each. The fingerprints will match.
- A few points are about the PlayOnline Viewer itself rather than Provenance: the login address it saves for new members, how it works out the Square Enix value, that polcore's login starts on port 51240, and that it checks its own version every time you connect. Those can't be shown from this repository, but you can watch the viewer's own connections the same way as below.
- You can see your saved entries in Control Panel > Credential Manager > Windows Credentials, as `Provenance:POL:...` and `Provenance:SEA:...`. Their user name is blank, and what's stored is the encrypted blob from step 1.
- You can watch the connections whilst you log in, with Resource Monitor's Network tab or Sysinternals TCPView, and match them up against this page.
- And if you'd rather not trust my builds at all, you can build Provenance yourself, [the README](https://github.com/Daleterrence/Provenance#building-it-yourself) has the steps.

If anything on this page doesn't match the code, or something just doesn't make sense, I'd **really** like to know. Open an issue, or if it's a security problem, report it privately as [SECURITY.md](https://github.com/Daleterrence/Provenance/blob/main/SECURITY.md) says.

[src]: https://github.com/Daleterrence/Provenance/tree/v1.0.0-pb.2.2/src
[watcher]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/watcher.c
[hookdir]: https://github.com/Daleterrence/Provenance/tree/v1.0.0-pb.2.2/native/hook

[save-vm]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/ViewModels/Main/AccountsViewModel.cs#L239-L240
[save-vm-edit]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/ViewModels/Main/AccountsViewModel.cs#L282-L286
[credsvc-save]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/CredentialService.cs#L65-L75
[credsvc-key]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/CredentialService.cs#L99-L103
[credtargets]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/CredentialTargets.cs#L5-L6
[wcs-format]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L176
[wcs-save]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L29-L70
[wcs-pack]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L220-L227
[dpapi]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/Dpapi.cs#L23-L25
[wcs-entropy]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L210-L216
[wcs-blank]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L49-L57
[wcs-write]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L194-L208
[credkey-load]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/CredentialKey.cs#L109-L124
[credkey-create]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/CredentialKey.cs#L139-L145
[credkey-path]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/CredentialKey.cs#L17-L24
[protfolders]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Security/ProtectedFolders.cs#L79-L92
[apppaths]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/AppPaths.cs#L13-L14
[locked]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Security/LockedFolder.cs#L539-L551
[manifest]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/app.manifest#L7
[acct-check]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/ViewModels/Main/AccountCheck.cs#L53-L73

[prepare]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/AccountRules.cs#L90-L98
[gamelaunch]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/GameLaunch.cs#L81-L92
[connect]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/PolSession.cs#L92-L106
[viewer-update]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/PolSession.cs#L169-L175
[hold]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/PolSession.cs#L210-L211
[signedbuilds]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Patcher/SignedBuilds.cs#L35-L50
[targets]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/LaunchCredentials.cs#L130-L151
[oneshot-save]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/LaunchCredentials.cs#L122-L128
[wcs-persist]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L56-L57
[buildenv]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/PolSession.cs#L286-L292
[setlogin]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/HookEnvironment.cs#L37-L65
[setbase]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/HookEnvironment.cs#L12-L27
[launcherstart]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/Watcher.cs#L50-L51
[watcher-main]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/watcher.c#L249-L251
[watcher-start]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/watcher.c#L128-L154
[watcher-resume]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/watcher.c#L97-L118
[prestart]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L309-L350
[gofile]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/HookEnvironment.cs#L85-L92
[hookdir-set]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/App.xaml.cs#L114
[prestart-allowed]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L311-L343
[watcher-windower]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/watcher.c#L163-L224
[windower-start]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Game/Windower/WindowerStart.cs#L32-L58
[ashita-apply]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Game/Ashita/AshitaLauncher.cs#L67
[applycontract]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/HookEnvironment.cs#L95-L103
[watcher-attach]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/watcher.c#L75-L95

[dllmain]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/pol_hook.c#L150-L187
[install]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/pol_hook.c#L130-L138
[menustub]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/pol_hook.c#L95-L105
[fakesession]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L563-L575
[login]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L547-L561
[pwlen]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L553-L558
[readdetails]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L67-L86
[pccredread]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/pol_cred.h#L63-L123
[readkey]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/pol_cred.h#L17-L29
[entropy]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/pol_cred.h#L50-L61
[pvw2]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/pol_cred.h#L77-L81
[plain-refused]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L40-L45
[consume]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L55-L66
[session-steps]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L494-L513
[seed]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L94-L129
[sha1]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L1-L8
[otp-six]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L113-L121
[authrun]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/login.c#L446-L475
[svc]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/polcore.h#L190-L197
[svc2]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/polcore.h#L591-L614
[addresses]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/addresses.c#L85-L121

[sockets]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/polcore_sockets.c
[sockets-hook]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/polcore_sockets.c#L12-L49
[polcore-connect]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/polcore_sockets.c#L1-L10
[red-host]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L1-L2
[red-start]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L250-L257
[dllmain-red]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/pol_hook.c#L174
[red-ask]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L87-L119
[red-aimed]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L259-L264
[red-connect]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L266-L326
[red-listen]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L121-L138
[red-serve]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/redirector.c#L140-L164
[dns]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/Prestarter.cs#L85-L95

[cleanup]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/PolSession.cs#L220-L228
[sweep-start]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/LaunchCredentials.cs#L29-L37
[sweep]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/LaunchCredentials.cs#L61-L76
[chat-save]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/LaunchCredentials.cs#L165-L179
[chat-read]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/chat_room.c#L344-L358

[otp-fetch]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/OtpSources.cs#L40-L64
[toolfinder]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/ToolFinder.cs#L18-L24
[op]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/OnePassword.cs#L19-L32
[bw]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/Bitwarden.cs#L15-L55
[bw-login]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/Bitwarden.cs#L109-L158
[otpdialog]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Views/Dialogs/OtpDialog.xaml.cs#L125-L129
[bw-unlock]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/Bitwarden.cs#L23-L31
[bw-session]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/Bitwarden.cs#L13
[bw-lock]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/Bitwarden.cs#L174-L190
[kp-proxy]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/KeePassXc.cs#L142-L150
[kp-fetch]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/KeePassXc.cs#L20-L67
[kp-getlogins]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/KeePassXcClient.cs#L75-L88
[kp-pick]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/KeePassXc.cs#L115-L127
[kp-pair]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/KeePassXc.cs#L69-L86

[redact-acct]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/ICredentialService.cs#L28-L44
[redact-req]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/LoginRequest.cs#L13-L17
[redact-stored]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Credentials/WindowsCredentialStore.cs#L245-L252
[redact-otp]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/OtpSources.cs#L9-L18
[mask]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/native/hook/credentials.c#L87-L93

[masked-name]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/AccountRules.cs#L38
[masked-use]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/ViewModels/Main/AccountsViewModel.cs#L205-L209
[refused]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Login/RefusedPasswords.cs#L30-L36
[otpreuse]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/OtpReuse.cs#L10-L22
[otpreuse-hash]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/OtpReuse.cs#L61-L68
[otpreuse-key]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/ViewModels/Main/AccountsViewModel.cs#L358-L360

[patchproduct]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Patcher/PatchProduct.cs#L26-L39
[polupdater]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Patcher/PolUpdater.cs#L123-L124
[patchconnector]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Patcher/PatchConnector.cs#L19-L31
[patchprotocol]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Patcher/PatchProtocol.cs#L34-L60
[gameinstaller]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Install/GameInstaller.cs#L101
[search]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Social/SearchServers.cs#L7-L32
[searchclient]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Social/SearchClient.cs#L15-L26
[banners]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Feeds/BannerImages.cs#L14-L27
[playonline]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Feeds/PlayOnlineSite.cs#L12-L22
[mirror]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Mirror/MirrorLayout.cs#L7
[mirror-builds]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Mirror/MirrorLayout.cs#L28
[selfupdate]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/SelfUpdate.cs#L15
[channel]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/ChannelSource.cs#L24-L25
[feed]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Feeds/FeedService.cs#L9
[windower]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Game/Windower/WindowerHttp.cs#L12-L13
[toolinstaller]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/ToolInstaller.cs#L15-L19
[toolinstaller-cdn]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/ToolInstaller.cs#L125
[tool-update]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/ToolInstaller.cs#L80-L101
[toolsig]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/ToolSignature.cs#L133
[op-update]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/OnePassword.cs#L31
[bw-update]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Otp/Bitwarden.cs#L54

[ci]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml
[ci-pack]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml#L24-L67
[ci-tagcheck]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml#L58-L62
[ci-draft]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml#L69-L79
[ci-check]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml#L106-L127
[ci-upload]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml#L153-L177
[ci-setup]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/.github/workflows/build.yml#L170-L187
[pack-clean]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/pack-release.ps1#L33-L35
[pack-build]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/pack-release.ps1#L69-L78
[pack-natives]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/pack-release.ps1#L89-L95
[pack-vpk]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/pack-release.ps1#L97-L106
[assets-pin]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/assets.json#L1-L4
[get-assets]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/get-assets.ps1#L43-L47
[apprelease]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Mirror/AppRelease.cs#L6-L7
[releasegate]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/ReleaseCheck/ReleaseGate.cs#L16-L33
[releasegate-asset]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/tools/ReleaseCheck/ReleaseGate.cs#L84-L100
[hastoupdate]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/SelfUpdate.cs#L32
[update-flow]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/SelfUpdate.cs#L157-L170
[channel-download]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/ChannelSource.cs#L102-L117
[channel-refuse]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/ChannelSource.cs#L135-L164
[keys]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Mirror/MirrorKeys.cs#L7-L15
[signedblob]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Mirror/SignedBlob.cs#L31-L52
[apprelease-check]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Common/Mirror/AppRelease.cs#L37-L63
[selfupdate-check]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/SelfUpdate.cs#L288-L304
[channel-fallback]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/src/Provenance.Wpf/Services/ChannelSource.cs#L35-L100
[shortrev]: https://github.com/Daleterrence/Provenance/blob/v1.0.0-pb.2.2/Directory.Build.props#L49-L54
