"""Build one simple, fully offline ArdUi release."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "v3.pre8"
ARD_VERSION = "3.0.0-pre.2"
FRD_ARCHIVE = Path("D:/pub/FRD/v2.pre3.zip")
OUTPUT = Path("D:/pub/ArdUi") / f"{VERSION}.zip"
STAGE = ROOT / "dist" / f"{VERSION}-offline"
PUBLISH = ROOT / "dist" / f"final-{VERSION}"


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    assert not OUTPUT.exists() and not OUTPUT.with_suffix(".zip.sha256").exists(), "Refusing to overwrite published release"
    assert not STAGE.exists() and not PUBLISH.exists(), "Refusing to overwrite staging or publish output"
    assert FRD_ARCHIVE.is_file(), "FRD release is missing"
    subprocess.run([
        "dotnet", "publish", str(ROOT / "ArdUi.csproj"), "-c", "Release", "-r", "win-x64",
        "--self-contained", "false", "-o", str(PUBLISH), "-p:PublishSingleFile=true", "-p:PublishTrimmed=false",
    ], check=True)
    ui = PUBLISH / "ArdUi.exe"
    ard = ROOT.parent / "target" / "release" / "ard.exe"
    assert ui.is_file() and ard.is_file(), "Missing published ArdUi.exe or ARD runtime"
    assert subprocess.check_output([str(ard), "--version"], text=True).strip() == f"ard {ARD_VERSION}"
    payload = STAGE / "payload"
    (payload / "frd").mkdir(parents=True)
    shutil.copy2(ui, payload / "ArdUi.exe")
    shutil.copy2(ard, payload / "ard.exe")
    shutil.copy2(ROOT / "config.json", payload / "config.json")
    shutil.copy2(ROOT / "THIRD-PARTY-NOTICES.md", payload / "THIRD-PARTY-NOTICES.md")
    with zipfile.ZipFile(FRD_ARCHIVE) as archive:
        for entry in archive.infolist():
            path = Path(entry.filename)
            if entry.is_dir():
                continue
            assert not path.is_absolute() and ".." not in path.parts, "Invalid FRD archive path"
            target = payload / "frd" / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(entry))
    assert (payload / "frd" / "FRD.exe").is_file(), "FRD.exe is missing"
    subprocess.run([str(payload / "ArdUi.exe"), "--self-test"], check=True)
    shutil.copy2(ROOT / "install.ps1", STAGE / "install.ps1")
    shutil.copy2(ROOT / "packaging" / "offline" / "install.bat", STAGE / "install.bat")
    (STAGE / "RELEASE.txt").write_text(
        "ArdUi v3.pre8\n"
        "Extract this one archive and run install.bat.\n"
        "The installer copies all included files into versions\\v3.pre8 and retains an existing data directory.\n"
        "Requires preinstalled .NET 8 and .NET 10 x64 runtimes.\n",
        encoding="utf-8",
    )
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(OUTPUT, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(STAGE.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(STAGE).as_posix())
    OUTPUT.with_suffix(".zip.sha256").write_text(f"{sha(OUTPUT)} *{OUTPUT.name}\n", encoding="ascii")
    print(f"{OUTPUT}\nSHA256 {sha(OUTPUT)}\n{OUTPUT.stat().st_size / 1048576:.2f} MiB")


if __name__ == "__main__":
    main()
