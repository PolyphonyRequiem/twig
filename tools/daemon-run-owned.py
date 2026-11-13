"""Windows-only owned-command deadline, including descendants retaining output."""
import argparse
import ctypes
from ctypes import wintypes as w
import os
from pathlib import Path
import subprocess
import sys
import time

if os.name != "nt" or sys.version_info < (3, 12):
    raise SystemExit("Existing Windows Python 3.12+ is required; no tooling was installed.")

kernel = ctypes.WinDLL("kernel32", use_last_error=True)


class Limits(ctypes.Structure):
    _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
                ("flags", w.DWORD), ("min_working_set", ctypes.c_size_t),
                ("max_working_set", ctypes.c_size_t), ("active_limit", w.DWORD),
                ("affinity", ctypes.c_size_t), ("priority", w.DWORD),
                ("scheduling", w.DWORD)]


class IoCounters(ctypes.Structure):
    _fields_ = [(name, ctypes.c_uint64) for name in
                ("reads", "writes", "others", "read_bytes", "write_bytes", "other_bytes")]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [("basic", Limits), ("io", IoCounters),
                ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]


class Accounting(ctypes.Structure):
    _fields_ = [(name, ctypes.c_int64) for name in
                ("user", "kernel", "period_user", "period_kernel")] + [
                (name, w.DWORD) for name in ("faults", "total", "active", "terminated")]


class SecurityAttributes(ctypes.Structure):
    _fields_ = [("length", w.DWORD), ("descriptor", w.LPVOID), ("inherit", w.BOOL)]


def api(name, result, *arguments):
    function = getattr(kernel, name)
    function.restype = result
    function.argtypes = arguments
    return function


create_job = api("CreateJobObjectW", w.HANDLE, w.LPVOID, w.LPCWSTR)
set_job = api("SetInformationJobObject", w.BOOL, w.HANDLE, ctypes.c_int, w.LPVOID, w.DWORD)
query_job = api("QueryInformationJobObject", w.BOOL, w.HANDLE, ctypes.c_int, w.LPVOID, w.DWORD, w.LPVOID)
assign_job = api("AssignProcessToJobObject", w.BOOL, w.HANDLE, w.HANDLE)
terminate_job = api("TerminateJobObject", w.BOOL, w.HANDLE, w.UINT)
close_handle = api("CloseHandle", w.BOOL, w.HANDLE)
create_event = api("CreateEventW", w.HANDLE, ctypes.POINTER(SecurityAttributes), w.BOOL, w.BOOL, w.LPCWSTR)
set_event = api("SetEvent", w.BOOL, w.HANDLE)
wait_handle = api("WaitForSingleObject", w.DWORD, w.HANDLE, w.DWORD)
open_process = api("OpenProcess", w.HANDLE, w.DWORD, w.BOOL, w.DWORD)


def checked(value):
    if not value:
        raise ctypes.WinError(ctypes.get_last_error())
    return value


class OwnedJob:
    def __init__(self):
        self.handle = checked(create_job(None, None))
        try:
            limits = ExtendedLimits()
            limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; never allow breakaway.
            checked(set_job(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)))
        except BaseException:
            close_handle(self.handle)
            raise

    def start(self, command):
        attributes = SecurityAttributes(ctypes.sizeof(SecurityAttributes), None, True)
        gate = checked(create_event(ctypes.byref(attributes), True, False, None))
        process = None
        try:
            # The trusted worker cannot spawn the command until assigned; no native-start race.
            process = subprocess.Popen(
                [sys.executable, str(Path(__file__).resolve()), "--worker", str(gate), "--", *command],
                stdin=sys.stdin, stdout=sys.stdout, stderr=sys.stderr, close_fds=False)
            handle = checked(open_process(0x0101, False, process.pid))  # SET_QUOTA | TERMINATE
            try:
                checked(assign_job(self.handle, handle))
            finally:
                close_handle(handle)
            checked(set_event(gate))
            return process
        except BaseException:
            if process is not None:
                # Before assignment the worker is gated and cannot have descendants.
                process.kill()
                process.wait(timeout=2)
            raise
        finally:
            close_handle(gate)

    def active(self):
        accounting = Accounting()
        checked(query_job(self.handle, 1, ctypes.byref(accounting), ctypes.sizeof(accounting), None))
        return accounting.active

    def stop(self):
        if self.active():
            checked(terminate_job(self.handle, 124))
        deadline = time.monotonic() + 2
        while self.active():
            if time.monotonic() >= deadline:
                raise TimeoutError("owned job cleanup unconfirmed after two-second reap budget")
            time.sleep(0.01)

    def close(self):
        checked(close_handle(self.handle))


def worker(arguments):
    gate = int(arguments[0])
    try:
        if wait_handle(gate, 5000) != 0:
            raise TimeoutError("supervisor did not release the assigned worker")
    finally:
        close_handle(gate)
    # Interactive callers keep their stdin; installers explicitly close it before this point.
    return subprocess.call(arguments[2:], stdin=sys.stdin, stdout=sys.stdout, stderr=sys.stderr)


def main():
    if sys.argv[1:2] == ["--worker"]:
        return worker(sys.argv[2:])
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=int, required=True, choices=range(1, 601))
    parser.add_argument("command", nargs=argparse.REMAINDER)
    arguments = parser.parse_args()
    command = arguments.command
    if command[:1] == ["--"]:
        command = command[1:]
    if not command:
        parser.error("provide a command after --")
    deadline = time.monotonic() + arguments.timeout
    job = OwnedJob()
    code = 125
    try:
        process = job.start(command)
        try:
            code = process.wait(timeout=max(0, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            code = 124
            print(f"OWNED-DEADLINE: {arguments.timeout}s; exit=124", file=sys.stderr, flush=True)
        except KeyboardInterrupt:
            code = 130
    finally:
        try:
            job.stop()
            if code in (124, 130):
                print("OWNED-CLEANUP: active=0", file=sys.stderr, flush=True)
        except (OSError, TimeoutError) as error:
            print(f"OWNED-CLEANUP: UNCONFIRMED; {error}", file=sys.stderr, flush=True)
            if code not in (124, 130):
                code = 125
        finally:
            # Also reaps residual descendants after normal root exit, before output pipes drain.
            job.close()
    return code


if __name__ == "__main__":
    sys.exit(main())
