#!/usr/bin/env python3
"""Download and verify only the approved .NET dependency-transfer packages."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile

FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer"
MAX_PACKAGE_BYTES = 64 * 1024 * 1024
MAX_NUSPEC_BYTES = 1024 * 1024
PACKAGES = (
    ("Microsoft.NET.ILLink.Tasks", "10.0.14"),
    ("Microsoft.NET.ILLink.Tasks", "11.0.0-rc.3.26522.148"),
    ("Microsoft.NETCore.App.Runtime.win-x64", "11.0.0-rc.3.26522.148"),
)
RUNTIME_COMPLETION_PACKAGES = (
    ("Microsoft.DotNet.ILCompiler", "11.0.0-rc.3.26522.148"),
    ("Microsoft.WindowsDesktop.App.Runtime.win-x64", "11.0.0-rc.3.26522.148"),
    ("Microsoft.AspNetCore.App.Runtime.win-x64", "11.0.0-rc.3.26522.148"),
    ("Microsoft.NETCore.App.Runtime.NativeAOT.win-x64", "11.0.0-rc.3.26522.148"),
    ("runtime.win-x64.Microsoft.DotNet.ILCompiler", "11.0.0-rc.3.26522.148"),
)
PACKAGE_SETS = {
    "initial": PACKAGES,
    "runtime-completion": RUNTIME_COMPLETION_PACKAGES,
}
PACKAGE_CAP_BYTES = {
    package: MAX_PACKAGE_BYTES for package in PACKAGES
} | {
    RUNTIME_COMPLETION_PACKAGES[0]: 256 * 1024 * 1024,
    RUNTIME_COMPLETION_PACKAGES[1]: 128 * 1024 * 1024,
    RUNTIME_COMPLETION_PACKAGES[2]: 128 * 1024 * 1024,
    RUNTIME_COMPLETION_PACKAGES[3]: 128 * 1024 * 1024,
    RUNTIME_COMPLETION_PACKAGES[4]: 256 * 1024 * 1024,
}
TRANSFER_CAP_BYTES = {
    "initial": 3 * MAX_PACKAGE_BYTES,
    "runtime-completion": 896 * 1024 * 1024,
}


def curl(url, destination, max_filesize):
    destination.unlink(missing_ok=True)
    result = subprocess.run(
        ["curl", "--fail", "--silent", "--show-error", "--max-time", "30",
         "--max-filesize", str(max_filesize),
         "--output", str(destination), "--write-out", "%{http_code}", url],
        text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=35,
        check=False,
    )
    return result.returncode, result.stdout.strip(), result.stderr.strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def sha512(path):
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.digest()


def local_name(tag):
    return tag.rsplit("}", 1)[-1]


def nuspec_identity(path):
    with zipfile.ZipFile(path) as archive:
        matches = [entry for entry in archive.infolist()
                   if entry.filename.lower().endswith(".nuspec")]
        if len(matches) != 1 or "/" in matches[0].filename or "\\" in matches[0].filename:
            raise ValueError("Package must contain exactly one root-level nuspec.")
        entry = matches[0]
        if entry.file_size > MAX_NUSPEC_BYTES:
            raise ValueError("Package nuspec exceeds the 1 MiB validation bound.")
        root = ET.fromstring(archive.read(entry))
        metadata = next((node for node in root if local_name(node.tag) == "metadata"), None)
        if metadata is None:
            raise ValueError("Package nuspec metadata is missing.")
        values = {
            local_name(node.tag): (node.text or "").strip()
            for node in metadata
            if local_name(node.tag) in {"id", "version"}
        }
        if not values.get("id") or not values.get("version"):
            raise ValueError("Package nuspec ID or version is missing.")
        return values["id"], values["version"]


def verify_package(path, package_id, version, official_sha512=None):
    package = (package_id, version)
    if package not in PACKAGE_CAP_BYTES:
        raise ValueError("Package is not in the approved exact ID/version allowlist.")
    cap = PACKAGE_CAP_BYTES[package]
    if not path.is_file() or path.stat().st_size == 0 or path.stat().st_size > cap:
        raise ValueError(f"Package is empty, missing, or exceeds its {cap}-byte cap.")
    actual_id, actual_version = nuspec_identity(path)
    if (actual_id, actual_version) != (package_id, version):
        raise ValueError("Package nuspec ID/version does not match the approved package.")
    actual_sha512 = sha512(path)
    official_matches = None
    if official_sha512 is not None:
        try:
            expected = base64.b64decode(official_sha512.strip(), validate=True)
        except (ValueError, base64.binascii.Error) as error:
            raise ValueError("Official .sha512 value is not valid base64.") from error
        if len(expected) != hashlib.sha512().digest_size:
            raise ValueError("Official .sha512 value has the wrong digest length.")
        official_matches = actual_sha512 == expected
        if not official_matches:
            raise ValueError("Computed SHA-512 does not match NuGet's official .sha512.")
    return {
        "packageId": package_id,
        "version": version,
        "sizeBytes": path.stat().st_size,
        "packageCapBytes": cap,
        "sha256": sha256(path),
        "sha512": actual_sha512.hex(),
        "officialSha512": official_sha512.strip() if official_sha512 is not None else None,
        "officialSha512Matched": official_matches,
    }


def download_one(output, source_sha, package_id, version):
    cap = PACKAGE_CAP_BYTES.get((package_id, version))
    if cap is None:
        raise ValueError("Package is not in the approved exact ID/version allowlist.")
    slug = package_id.lower()
    filename = f"{slug}.{version}.nupkg"
    url = f"{FLAT_CONTAINER}/{slug}/{version.lower()}/{filename}"
    package_path = output / filename
    status, http_status, error = curl(url, package_path, cap)
    if status != 0 or http_status != "200":
        raise RuntimeError(f"NuGet package request failed (HTTP {http_status or 'unknown'}): {error}")

    checksum_path = output / f"{filename}.sha512"
    checksum_url = f"{url}.sha512"
    checksum_exit, checksum_status, checksum_error = curl(checksum_url, checksum_path, 256)
    if checksum_status == "200" and checksum_exit == 0:
        if checksum_path.stat().st_size > 256:
            raise ValueError("Official .sha512 response exceeds the 256-byte bound.")
        try:
            official = checksum_path.read_text(encoding="ascii").strip()
        except UnicodeError as error:
            raise ValueError("Official .sha512 response is not ASCII.") from error
        checksum_receipt_status = "200"
    elif checksum_status == "404" and checksum_exit == 22:
        checksum_path.unlink(missing_ok=True)
        official = None
        checksum_receipt_status = "404"
    else:
        raise RuntimeError(
            f"NuGet .sha512 request failed (HTTP {checksum_status or 'unknown'}): {checksum_error}")

    receipt = verify_package(package_path, package_id, version, official)
    receipt.update({
        "sourceCommit": source_sha,
        "packageUrl": url,
        "packageHttpStatus": http_status,
        "officialSha512Url": checksum_url,
        "officialSha512HttpStatus": checksum_receipt_status,
    })
    receipt_path = output / f"{filename}.receipt.json"
    receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--package-set", choices=PACKAGE_SETS, default="initial")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_sha):
        raise ValueError("Source SHA must be a full lowercase 40-character commit hash.")
    output = args.output_dir
    output.parent.mkdir(parents=True, exist_ok=True)
    output.mkdir(exist_ok=False)
    receipts = []
    for package_id, version in PACKAGE_SETS[args.package_set]:
        receipts.append(download_one(output, args.source_sha, package_id, version))
        total_size = sum(receipt["sizeBytes"] for receipt in receipts)
        if total_size > TRANSFER_CAP_BYTES[args.package_set]:
            raise ValueError("Transfer exceeds its aggregate byte cap.")
    manifest = {
        "sourceCommit": args.source_sha,
        "packageSet": args.package_set,
        "totalSizeBytes": sum(receipt["sizeBytes"] for receipt in receipts),
        "transferCapBytes": TRANSFER_CAP_BYTES[args.package_set],
        "packages": receipts,
        "networkPolicy": "official NuGet flat container; standard curl TLS; no redirects or retries",
    }
    (output / "transfer-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError, zipfile.BadZipFile, ET.ParseError) as error:
        raise SystemExit(f"NuGet dependency transfer failed: {error}") from error
