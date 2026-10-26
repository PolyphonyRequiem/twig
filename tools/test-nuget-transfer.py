"""Offline checks for the exact-package NuGet transfer validator."""
import hashlib
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "nuget_transfer", Path(__file__).with_name("nuget-transfer.py"))
transfer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(transfer)


def package(path, package_id, version):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(f"{package_id}.nuspec",
                         f"<package><metadata><id>{package_id}</id><version>{version}</version></metadata></package>")


class NugetTransferTests(unittest.TestCase):
    def test_curl_enforces_package_and_checksum_download_caps(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / "download"
            for cap in (transfer.MAX_PACKAGE_BYTES, 256):
                with self.subTest(cap=cap), patch.object(transfer.subprocess, "run") as run:
                    run.return_value = subprocess.CompletedProcess([], 0, "200", "")
                    self.assertEqual(transfer.curl("https://example.invalid/package", destination, cap),
                                     (0, "200", ""))
                    self.assertEqual(run.call_args.args[0][run.call_args.args[0].index("--max-filesize") + 1],
                                     str(cap))
                    self.assertEqual(run.call_args.kwargs["timeout"], 35)

    def test_checksum_404_is_optional_only_for_curl_http_failure(self):
        def package_then_success(url, destination, _max_filesize):
            package(destination, *transfer.PACKAGES[0])
            return 0, "200", ""

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            def package_then_404(url, destination, max_filesize):
                result = package_then_success(url, destination, max_filesize)
                return result if destination.suffix == ".nupkg" else (
                    22, "404", "curl: (22) The requested URL returned error: 404")

            with patch.object(transfer, "curl", side_effect=package_then_404) as curl:
                receipt = transfer.download_one(output, "a" * 40, *transfer.PACKAGES[0])
            self.assertEqual(receipt["officialSha512HttpStatus"], "404")
            self.assertIsNone(receipt["officialSha512"])
            self.assertEqual(curl.call_args_list[0].args[2], transfer.MAX_PACKAGE_BYTES)
            self.assertEqual(curl.call_args_list[1].args[2], 256)

        for curl_result in ((35, "", "TLS failure"), (7, "", "connection failure"),
                            (22, "403", "HTTP 403"), (63, "200", "file too large")):
            with self.subTest(curl_result=curl_result), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                def package_then_failure(url, destination, max_filesize):
                    result = package_then_success(url, destination, max_filesize)
                    return result if destination.suffix == ".nupkg" else curl_result

                with patch.object(transfer, "curl", side_effect=package_then_failure):
                    with self.assertRaisesRegex(RuntimeError, "sha512 request failed"):
                        transfer.download_one(output, "a" * 40, *transfer.PACKAGES[0])

    def test_exact_nuspec_and_official_sha512_are_verified_without_extraction(self):
        package_id, version = transfer.PACKAGES[0]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "package.nupkg"
            package(path, package_id, version)
            official = transfer.base64.b64encode(hashlib.sha512(path.read_bytes()).digest()).decode()
            receipt = transfer.verify_package(path, package_id, version, official)
            self.assertEqual(receipt["packageId"], package_id)
            self.assertEqual(receipt["version"], version)
            self.assertTrue(receipt["officialSha512Matched"])
            self.assertEqual(list(Path(directory).iterdir()), [path])

    def test_rejects_unapproved_package_or_mismatched_nuspec_version(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "package.nupkg"
            package(path, *transfer.PACKAGES[0])
            with self.assertRaisesRegex(ValueError, "allowlist"):
                transfer.verify_package(path, "Microsoft.NET.ILLink.Tasks", "10.0.15")
            with self.assertRaisesRegex(ValueError, "nuspec ID/version"):
                transfer.verify_package(path, *transfer.PACKAGES[1])

    def test_rejects_bad_official_digest(self):
        package_id, version = transfer.PACKAGES[0]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "package.nupkg"
            package(path, package_id, version)
            with self.assertRaisesRegex(ValueError, "does not match"):
                transfer.verify_package(path, package_id, version, transfer.base64.b64encode(b"x" * 64).decode())


if __name__ == "__main__":
    unittest.main()
