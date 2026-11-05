#!/usr/bin/env python3
"""Install a trusted linux-x64 release. Requires PostgreSQL 16.

Run as root: python3 install.py release.tar.gz SHA256
The adjacent bigbluefish-sync.service must also be uploaded.
Does not change SSH, Tailscale, network routes, or existing database contents.
"""
from pathlib import Path
import datetime
import hashlib
import os
import pwd
import secrets
import subprocess
import sys
import tarfile
import time
import urllib.request


def command(args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


if os.geteuid() != 0 or len(sys.argv) != 3:
    raise SystemExit("Usage (root): python3 install.py release.tar.gz SHA256")
archive = Path(sys.argv[1]).resolve(strict=True)
if hashlib.sha256(archive.read_bytes()).hexdigest().lower() != sys.argv[2].lower():
    raise SystemExit("Release SHA256 mismatch; nothing installed")
unit = Path(__file__).with_name("bigbluefish-sync.service").read_text()
runtime = Path("/opt/bigbluefish-sync/dotnet/dotnet")
if not runtime.exists():
    raise SystemExit("Install the verified ASP.NET runtime under /opt/bigbluefish-sync/dotnet first")
command(["pg_isready"])
try:
    pwd.getpwnam("fishsync")
except KeyError:
    command(["useradd", "--system", "--home-dir", "/var/lib/bigbluefish-sync",
             "--shell", "/usr/sbin/nologin", "fishsync"])

config_dir = Path("/etc/bigbluefish-sync")
config_dir.mkdir(mode=0o700, exist_ok=True)
env_path = config_dir / "server.env"
if not env_path.exists():
    # Refuse to adopt a pre-existing database/role without explicit migration.
    check = command(["runuser", "-u", "postgres", "--", "psql", "-At", "-c",
                     "SELECT (EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fish')) "
                     "OR (EXISTS(SELECT 1 FROM pg_database WHERE datname='fish'))"],
                    capture_output=True, text=True)
    if check.stdout.strip() != "f":
        raise SystemExit("Existing fish role/database found; migration required, left untouched")
    password = secrets.token_hex(32)
    sql = f"CREATE ROLE fish LOGIN PASSWORD '{password}';\nCREATE DATABASE fish OWNER fish;\n"
    result = subprocess.run(["runuser", "-u", "postgres", "--", "psql",
                             "-v", "ON_ERROR_STOP=1"], input=sql,
                            capture_output=True, text=True)
    if result.returncode:
        raise SystemExit("Database initialization failed; inspect PostgreSQL locally (credentials not printed)")
    content = (f"SYNC_DATABASE_URL=Host=127.0.0.1;Username=fish;Password={password};Database=fish\n"
               f"SYNC_ADMIN_TOKEN={secrets.token_hex(32)}\n"
               "ASPNETCORE_URLS=http://127.0.0.1:8080\n"
               "ASPNETCORE_ENVIRONMENT=Production\n"
               "DOTNET_EnableDiagnostics=0\n")
    fd = os.open(env_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as stream:
        stream.write(content)
else:
    if env_path.stat().st_mode & 0o077:
        raise SystemExit("server.env permissions must be 0600 before continuing")

release_root = Path("/opt/bigbluefish-sync/releases")
release_root.mkdir(parents=True, exist_ok=True)
release = release_root / (datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
                          + "-" + sys.argv[2][:12])
release.mkdir(mode=0o755)
with tarfile.open(archive, "r:gz") as package:
    for item in package.getmembers():
        target = (release / item.name).resolve()
        if not target.is_relative_to(release) or not (item.isfile() or item.isdir()):
            raise SystemExit("Unexpected archive entry; release not activated")
    package.extractall(release, filter="data")
binary = release / "BigBlueFish.SyncServer"
if binary.read_bytes()[:4] != b"\x7fELF":
    raise SystemExit("Expected Linux ELF binary; release not activated")
for item in release.rglob("*"):
    os.chown(item, 0, 0)
    os.chmod(item, 0o755 if item.is_dir() else 0o644)
os.chmod(binary, 0o755)
current = Path("/opt/bigbluefish-sync/current")
if current.exists() and not current.is_symlink():
    raise SystemExit("Current release is not a symlink; left untouched")
temporary = current.with_name(".current-" + secrets.token_hex(8))
temporary.symlink_to(release)
os.replace(temporary, current)
Path("/etc/systemd/system/bigbluefish-sync.service").write_text(unit)
command(["systemctl", "daemon-reload"])
command(["systemctl", "enable", "bigbluefish-sync"])
command(["systemctl", "restart", "bigbluefish-sync"])
for attempt in range(20):
    try:
        with urllib.request.urlopen("http://127.0.0.1:8080/health", timeout=2) as response:
            if response.status == 200:
                print("Installed; loopback /health returned HTTP 200. No credentials printed.")
                break
    except OSError:
        time.sleep(1)
else:
    raise SystemExit("Service health check failed; inspect systemctl status bigbluefish-sync")
