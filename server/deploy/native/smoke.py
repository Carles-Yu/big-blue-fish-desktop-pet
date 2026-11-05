#!/usr/bin/env python3
"""Root-only loopback smoke test. Creates synthetic data and revokes test devices.

Requires python3-cryptography. Does not print tokens, pair codes or archive keys.
Uses a fresh random profile, then resets only that profile in finally.
"""
from pathlib import Path
import base64
import concurrent.futures
import json
import os
import urllib.error
import urllib.request
import uuid
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


settings = dict(line.split("=", 1) for line in
                Path("/etc/bigbluefish-sync/server.env").read_text().splitlines() if "=" in line)
admin = settings["SYNC_ADMIN_TOKEN"]
base = "http://127.0.0.1:8080"
profile = str(uuid.uuid4())
devices = []
checks = 0


def request(path, token=None, body=None, method=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=8) as response:
        return json.load(response)


def check(condition, label):
    global checks
    if not condition:
        raise RuntimeError(label)
    checks += 1
    print("PASS " + label, flush=True)


def pair(name):
    code = request("/v1/admin/pairings", admin, {})["code"]
    device = request("/v1/device-pairings/complete", body={"code": code, "deviceName": name})
    devices.append(device)
    return device["deviceToken"]


try:
    check(request("/health")["status"] == "ok", "service health")
    try:
        request("/v1/devices")
        check(False, "unauthenticated requests rejected")
    except urllib.error.HTTPError as error:
        check(error.code == 401, "unauthenticated requests rejected")
    first = pair("deployment-smoke-A")
    second = pair("deployment-smoke-B")
    check(devices[0]["deviceId"] != devices[1]["deviceId"], "independent device pairing")
    record = str(uuid.uuid4())
    key = os.urandom(32)
    nonce = os.urandom(12)
    plain = b'{"content":"synthetic deployment check"}'
    aad = (uuid.UUID(profile).hex + "|" + uuid.UUID(record).hex + "|1|1").encode()
    cipher = AESGCM(key).encrypt(nonce, plain, aad)
    operation = dict(operationId=str(uuid.uuid4()), recordId=record, baseRevision=0,
                     kind="upsert", revision=1, ciphertext=base64.b64encode(cipher).decode(),
                     nonce=base64.b64encode(nonce).decode(), deleted=False)

    def push(op, token=first):
        return request("/v1/sync/push", token, dict(profileId=profile, epoch=1, operations=[op]))[0]

    accepted = push(operation)
    check(accepted["status"] == 0, "encrypted memory write")
    duplicate = push(operation)
    check(duplicate["status"] == 0 and duplicate["serverSequence"] == accepted["serverSequence"],
          "idempotent retry")
    changes = request(f"/v1/sync/pull?profileId={profile}&after=0&epoch=1", second)["changes"]
    check(len(changes) == 1, "second device pull from real PostgreSQL")
    change = changes[0]
    decrypted = AESGCM(key).decrypt(base64.b64decode(change["nonce"]),
                                   base64.b64decode(change["ciphertext"]), aad)
    check(decrypted == plain, "AES-GCM decrypt on receiving device")
    stale = dict(operation, operationId=str(uuid.uuid4()), revision=2)
    check(push(stale, second)["status"] == 2, "stale edit conflicts")
    deletion = dict(operation, operationId=str(uuid.uuid4()), kind="delete", revision=2,
                    baseRevision=1, ciphertext=None, nonce=None, deleted=True)
    check(push(deletion, second)["status"] == 0, "cross-device deletion")
    revived = dict(operation, operationId=str(uuid.uuid4()), baseRevision=2, revision=3)
    check(push(revived)["status"] == 3, "deleted record cannot be revived")
    concurrent_record = str(uuid.uuid4())
    contender_a = dict(operation, recordId=concurrent_record, operationId=str(uuid.uuid4()))
    contender_b = dict(contender_a, operationId=str(uuid.uuid4()))
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        futures = [pool.submit(push, contender_a, first), pool.submit(push, contender_b, second)]
        outcomes = [future.result()["status"] for future in futures]
    check(sorted(outcomes) == [0, 2], "concurrent writes atomically accept one version and conflict the other")
    request(f"/v1/profiles/{profile}/reset", admin, {})
    check(push(dict(operation, operationId=str(uuid.uuid4())))["status"] == 4,
          "old epoch rejected after reset")
    print(f"REMOTE-SMOKE-OK {checks}", flush=True)
finally:
    # Fresh profile and test devices only; never reset any existing user profile.
    if devices:
        request(f"/v1/profiles/{profile}/reset", admin, {})
        for device in devices:
            request("/v1/devices/" + device["deviceId"], device["deviceToken"], method="DELETE")
        print("Synthetic profile reset; test device tokens revoked.", flush=True)
