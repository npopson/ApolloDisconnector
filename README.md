# Apollo Disconnector

A small Windows utility for Apollo/Sunshine hosts. It listens for local keyboard input and calls Apollo's local API to close the active streaming app/session, which should run Apollo's normal quit/undo path and unblack the host display.

## First Run

Run `ApolloDisconnector.exe`. The first-run setup asks for your Apollo Web UI URL, username, and password; values shown in `[brackets]` are defaults and can be accepted by pressing Enter.

After setup, it starts a hidden background copy and tells you when the setup window can be closed. In normal use there is no terminal window. When it detects a local keyboard key press and Apollo has an active session, it asks Apollo to close that session.

To rerun setup later:

```powershell
ApolloDisconnector.exe --setup
```

To uninstall startup integration and saved settings:

```powershell
ApolloDisconnector.exe --uninstall
```

## Publish

```powershell
.\scripts\Publish.ps1
```

The portable package will be under `dist\ApolloDisconnector`. Settings are created on first run, so the package does not need a pre-edited JSON file.

The default package is framework-dependent, so the host needs the .NET 8 Desktop Runtime or a compatible newer runtime installed. To build a fully self-contained package, run:

```powershell
.\scripts\Publish.ps1 -SelfContained
```

## Notes

- The utility uses Windows Raw Input and logs keyboard device names the first time it sees them.
- By default, keyboard devices reported as `<unknown>` are ignored, which avoids disconnecting on Moonlight/iPad typing in common setups.
- If remote input still triggers a disconnect, rerun setup and edit `%AppData%\ApolloDisconnector\appsettings.json`: copy a unique substring from your physical keyboard's accepted device log into `LocalKeyboardDeviceNameContains`. When that list is set, only matching keyboard devices can disconnect Apollo.
- The Apollo HTTPS certificate is commonly self-signed locally, so invalid certificates are allowed only for loopback URLs by default.
- If Apollo returns `401 Unauthorized`, rerun setup with `ApolloDisconnector.exe --setup`.
