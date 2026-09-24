"""Static scope audit for the simple offline ArdUi installer."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def text(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    print("PASS:", message)


runtime = text("Transport/ArdProcess.cs")
installer = text("install.ps1")
release = text("packaging/build-local-release.py")
shares = text("Transport/WindowsShares.cs")
window = text("App/MainWindow.cs")
diagnostics = text("Core/Diagnostics.cs")

check(not re.search(r"[0-9a-f]{64}", runtime) and ".sha256" not in runtime,
      "ARD runtime has no embedded or sidecar checksum gate")
check(".sha256" not in diagnostics,
      "diagnostics do not require a sidecar checksum file")
check("Invoke-WebRequest" not in installer and "http://" not in installer and "https://" not in installer,
      "installer has no network download path")
check("Get-FileHash" not in installer and "SHA256" not in installer and "frd-current" not in installer,
      "installer has no payload hash, pointer, or version validation path")
check("Copy-Item -Path (Join-Path $payload '*') -Destination $destination -Recurse -Force" in installer,
      "installer copies the complete payload into the version directory")
check("if(-not (Test-Path -LiteralPath $data -PathType Container))" in installer and "--identity-store $data" in installer,
      "installer creates configuration and identity only on first installation")
check(installer.index("if(-not (Test-Path -LiteralPath $data") > installer.index("Copy-Item -Path"),
      "existing data is never changed during payload replacement")
check("CreateShortcut" in installer and "ArdUi.cmd" in installer,
      "installer updates the launcher and startup shortcut")
check("Start-Process -FilePath $launcher" in installer,
      "installer starts ArdUi after a successful installation")
check("ard.exe.sha256" not in release and "expectedArd" not in release,
      "local release builder ships ARD as a normal versioned payload file")
check("New-SmbMapping @argsMap" in shares and ".Credential" not in shares,
      "SMB mapping leaves credentials to Windows")
check("Windows账户密码" not in window and "Windows 会使用当前账户" in window,
      "file-sharing UI does not collect a Windows password")
