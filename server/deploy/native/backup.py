#!/usr/bin/env python3
"""Root-only authenticated PostgreSQL backups and isolated restore drills.

backup: creates an AES-256-GCM encrypted pg_dump; key never enters the dump.
verify FILE [--restore-test]: authenticates, checks pg_dump TOC, optionally restores
to a uniquely named temporary DB. Never restores over the live fish database.
"""
import argparse
import datetime
import hashlib
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import uuid
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAGIC = b"BBFPG01"
AAD = b"BigBlueFish PostgreSQL backup v1"
MAX_BYTES = 256 * 1024 * 1024


def pg(args, **kwargs):
    return subprocess.run(["runuser", "-u", "postgres", "--"] + args,
                          check=True, **kwargs)


def load_key(path, create):
    if not path.exists() and create:
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(fd, "wb") as stream:
            stream.write(secrets.token_bytes(32))
            stream.flush()
            os.fsync(stream.fileno())
    if path.stat().st_mode & 0o077:
        raise RuntimeError("Backup key must be private (0600)")
    key = path.read_bytes()
    if len(key) != 32:
        raise RuntimeError("Invalid backup key length")
    return key


def dump_to(path):
    with path.open("wb") as stream:
        pg(["pg_dump", "--format=custom", "--no-owner", "--no-privileges", "fish"],
           stdout=stream, timeout=120)
    if not 0 < path.stat().st_size <= MAX_BYTES:
        raise RuntimeError("Dump is empty or exceeds the 256 MiB limit; adjust backup capacity before continuing")
    subprocess.run(["pg_restore", "--list", str(path)], check=True, capture_output=True)


def backup(directory, key):
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="fish-backup-") as temporary:
        dump = Path(temporary) / "dump.pgdump"
        dump_to(dump)
        nonce = secrets.token_bytes(12)
        data = MAGIC + nonce + AESGCM(key).encrypt(nonce, dump.read_bytes(), AAD)
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        destination = directory / ("fish-" + stamp + "-" + uuid.uuid4().hex[:8] + ".pgdump.aesgcm")
        pending = destination.with_suffix(destination.suffix + ".tmp")
        fd = os.open(pending, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, destination)
        # Recheck authentication before publishing success or deleting old backups.
        if AESGCM(key).decrypt(nonce, data[len(MAGIC) + 12:], AAD) != dump.read_bytes():
            raise RuntimeError("Backup authentication verification failed")
        threshold = datetime.datetime.now().timestamp() - 28 * 24 * 3600
        for old in directory.glob("fish-*.pgdump.aesgcm"):
            if old.is_file() and not old.is_symlink() and old.stat().st_mtime < threshold:
                old.unlink()
        print(str(destination))
        print("SHA256=" + hashlib.sha256(data).hexdigest())


def verify(archive, key, restore_test):
    if archive.stat().st_size > MAX_BYTES + 128:
        raise RuntimeError("Archive too large")
    data = archive.read_bytes()
    if not data.startswith(MAGIC) or len(data) < len(MAGIC) + 12 + 16:
        raise RuntimeError("Invalid backup header")
    nonce = data[len(MAGIC):len(MAGIC) + 12]
    plain = AESGCM(key).decrypt(nonce, data[len(MAGIC) + 12:], AAD)
    with tempfile.TemporaryDirectory(prefix="fish-restore-check-") as temporary:
        dump = Path(temporary) / "dump.pgdump"
        dump.write_bytes(plain)
        os.chmod(dump, 0o600)
        subprocess.run(["pg_restore", "--list", str(dump)], check=True, capture_output=True)
        if restore_test:
            # Generated here, never supplied by an argument or read from the archive.
            name = "fish_restorecheck_" + uuid.uuid4().hex
            pg(["createdb", "--template=template0", name], timeout=30)
            try:
                with dump.open("rb") as stream:
                    pg(["pg_restore", "--exit-on-error", "--no-owner", "--no-privileges", "-d", name],
                       stdin=stream, capture_output=True, timeout=120)
                result = pg(["psql", "-At", "-d", name, "-c",
                             "SELECT count(*) FROM pg_tables WHERE schemaname='public'"],
                            capture_output=True, text=True, timeout=10)
                if int(result.stdout.strip()) < 7:
                    raise RuntimeError("Restored schema is incomplete")
                print("RESTORE-DRILL-OK: authenticated backup restored into an isolated temporary database")
            finally:
                pg(["dropdb", name], timeout=30)
    print("BACKUP-VERIFY-OK")


def main():
    if os.geteuid() != 0:
        raise RuntimeError("Run as root; no key or credential is printed")
    os.umask(0o077)
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["backup", "verify"])
    parser.add_argument("archive", nargs="?", type=Path)
    parser.add_argument("--restore-test", action="store_true")
    parser.add_argument("--key", type=Path, default=Path("/etc/bigbluefish-sync/backup.key"))
    parser.add_argument("--directory", type=Path, default=Path("/var/backups/bigbluefish-sync"))
    args = parser.parse_args()
    key = load_key(args.key, args.mode == "backup")
    if args.mode == "backup":
        backup(args.directory, key)
    else:
        if args.archive is None:
            parser.error("verify requires a backup file")
        verify(args.archive, key, args.restore_test)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Avoid emitting dump contents, query payloads or key bytes on failure.
        raise SystemExit("Backup operation failed: " + type(error).__name__)
