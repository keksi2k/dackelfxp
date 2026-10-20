"""Builds and packs the DackelFXP release for all targets.

    python tools/release.py                 # all targets
    python tools/release.py win-x64 osx-arm64

Writes releases/v<version>/ (version from Directory.Build.props):
  DackelFxp-<v>-win-<arch>.zip       DackelFxp.exe
  DackelFxp-<v>-linux-<arch>.tar.gz  DackelFxp + dackelfxp.png
  DackelFxp-<v>-osx-<arch>.zip       DackelFXP.app (unsigned, ad-hoc signed by the .NET SDK)
  SHA256SUMS.txt
Requires the .NET SDK and Pillow (for the macOS icon).
"""
import hashlib
import io
import re
import struct
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "DackelFxp.App"
ICON_1024 = ROOT / "tools" / "icon" / "dackelfxp-1024.png"
TARGETS = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]


def version():
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    return re.search(r"<Version>([^<]+)</Version>", props).group(1)


def publish(rid):
    out = ROOT / "artifacts" / rid
    subprocess.run(["dotnet", "publish", str(APP), "-c", "Release", "-r", rid, "-o", str(out), "-nologo", "-v", "q"], check=True)
    return out


def tar_add(tar, data, name, mode):
    info = tarfile.TarInfo(name)
    info.size, info.mode, info.mtime = len(data), mode, 0
    tar.addfile(info, io.BytesIO(data))


def zip_add(z, data, name, mode):
    info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # Unix, so that the permissions below are honored
    info.external_attr = (0o100000 | mode) << 16
    z.writestr(info, data)


def icns():
    """macOS icon: PNG images in an icns container."""
    src = Image.open(ICON_1024).convert("RGBA")
    chunks = b""
    for kind, size in [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)]:
        buf = io.BytesIO()
        src.resize((size, size), Image.LANCZOS).save(buf, "PNG")
        png = buf.getvalue()
        chunks += kind.encode() + struct.pack(">I", 8 + len(png)) + png
    return b"icns" + struct.pack(">I", 8 + len(chunks)) + chunks


def info_plist(v):
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DackelFXP</string>
  <key>CFBundleDisplayName</key><string>DackelFXP</string>
  <key>CFBundleIdentifier</key><string>io.github.keksi2k.dackelfxp</string>
  <key>CFBundleExecutable</key><string>DackelFxp</string>
  <key>CFBundleIconFile</key><string>dackelfxp</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{v}</string>
  <key>CFBundleVersion</key><string>{v}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
""".encode()


def pack(rid, out, v, dest):
    name = f"DackelFxp-{v}-{rid}"
    if rid.startswith("win-"):
        path = dest / f"{name}.zip"
        with zipfile.ZipFile(path, "w") as z:
            zip_add(z, (out / "DackelFxp.exe").read_bytes(), f"{name}/DackelFxp.exe", 0o755)
    elif rid.startswith("linux-"):
        path = dest / f"{name}.tar.gz"
        with tarfile.open(path, "w:gz") as t:
            tar_add(t, (out / "DackelFxp").read_bytes(), f"{name}/DackelFxp", 0o755)
            # icon for desktop launchers / menu entries
            tar_add(t, (APP / "Assets" / "dackelfxp.png").read_bytes(), f"{name}/dackelfxp.png", 0o644)
    else:
        path = dest / f"{name}.zip"
        with zipfile.ZipFile(path, "w") as z:
            zip_add(z, info_plist(v), "DackelFXP.app/Contents/Info.plist", 0o644)
            zip_add(z, (out / "DackelFxp").read_bytes(), "DackelFXP.app/Contents/MacOS/DackelFxp", 0o755)
            zip_add(z, icns(), "DackelFXP.app/Contents/Resources/dackelfxp.icns", 0o644)
    return path


def main():
    v = version()
    targets = sys.argv[1:] or TARGETS
    dest = ROOT / "releases" / f"v{v}"
    dest.mkdir(parents=True, exist_ok=True)
    for rid in targets:
        print(f"{rid} ...", flush=True)
        pack(rid, publish(rid), v, dest)
    archives = sorted(p for p in dest.iterdir() if p.name.startswith(f"DackelFxp-{v}-"))
    with open(dest / "SHA256SUMS.txt", "w", newline="\n") as sums:
        for p in archives:
            sums.write(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n")
    for p in archives:
        print(f"{p.name}  {p.stat().st_size / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
