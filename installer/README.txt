TypeTrail Windows installer

Build the application first, then run:
  powershell.exe -NoProfile -File installer\build-installer.ps1 -CompilerPath C:\path\to\ISCC.exe

Requires Inno Setup 7.1.0. Default input: bin/.
Output: dist/TypeTrail-Setup-1.0.0-x64.exe. Intermediate: .build/installer/.
The two user guides are taken from docs/ unless supplied in the payload.
Only explicitly listed runtime files, user guides and the Unicode license are
packaged. Personal history, settings and credentials must never be included.

Setup checks for .NET Framework 4.8 before stopping or replacing the application.
If it is missing, a localized message is shown; no runtime is downloaded.
The helper requests a normal exit and waits for process termination. It never
reads history, force-closes editors or replaces an unchanged native bridge.

The default installation is %LOCALAPPDATA%\Programs\TypeTrail. Shortcuts and the
Windows uninstall entry use the stable TypeTrail.Desktop identity. Data is stored
separately in %LOCALAPPDATA%\TypeTrail and is retained after uninstall.
Startup preferences are preserved on upgrade.
