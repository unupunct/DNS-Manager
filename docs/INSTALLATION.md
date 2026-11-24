# Installation

DNS Manager Pro runs on 64-bit Windows 10 and Windows 11. Release builds are **self-contained**: you do not need to install .NET.

## Option 1: installer (MSI)

1. Download `DNSManagerPro-<version>-win-x64.msi` from the project's Releases page.
2. Optional: compare its SHA-256 hash with `SHA256SUMS.txt` from the same release.

   ```powershell
   Get-FileHash .\DNSManagerPro-1.0.0-win-x64.msi -Algorithm SHA256
   ```

3. Double-click the MSI. Windows asks for administrator permission because the app is installed under `C:\Program Files\DNS Manager Pro`.
4. Choose the folder and features. A Start Menu shortcut is always created; the **Desktop shortcut** is optional and off by default.
5. Start *DNS Manager Pro* from the Start Menu.

The installer is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Check the hash first, then choose *More info*, *Run anyway*.

### Upgrading

Run the newer MSI. It replaces the old version in place and keeps your data.

### Uninstalling

Use *Settings, Apps, Installed apps* (or *Programs and Features*).

- The program files, shortcuts, and the "start with Windows" entry of the user who uninstalls are removed.
- **Your data is kept on purpose.** The database, settings and logs live in `%LocalAppData%\DNSManagerPro` and are not touched, so a reinstall picks up where you left off.
- To remove them as well, delete that folder after uninstalling. Before that, you can export a backup from *Settings, Data and privacy*.

## Option 2: portable zip

1. Download `DNSManagerPro-<version>-win-x64-portable.zip` and extract it anywhere.
2. Run `DNSManagerPro.exe`.

Nothing is installed or registered. Data is still stored per user in `%LocalAppData%\DNSManagerPro`. To remove the app, delete the extracted folder and, if you want, the data folder. If you turned on *Start with Windows*, turn it off in Settings first (otherwise the startup entry points to a missing file; it is harmless but untidy).

## Administrator rights

The app starts as a normal user. Reading DNS settings, benchmarking and diagnostics work without elevation. Changing DNS settings and flushing or reading the Windows DNS cache need administrator rights; when you apply a change the app explains why and offers *Restart as administrator* (a standard Windows UAC prompt).

## Building from source

Requires the .NET 10 SDK.

```powershell
dotnet build DNSManagerPro.slnx
dotnet test src/DNSManagerPro.Tests
dotnet run --project src/DNSManagerPro.App
```

To build the release files (portable zip, plus the MSI when the WiX v5 CLI `wix` and its UI extension are installed):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

The files are written to `dist\`. Add `-SkipInstaller` to build only the zip.
