r"""Saves a running game's main module as a PE file, for builds whose exe is packed on disk (Steam's RE1 is GOG's exe
inside a wrapper that unpacks it at start). Read-only: it opens the process for reading and nothing else.

    python tools/re1/dump_image.py <pid> <out.exe>

The file is the image as it is in memory, with each section's file offset set to its address, so tools that read
an exe (tools/re1/xmap.py, port_profile.py, pefile) see the unpacked code. Unreadable pages are written as zeros.
The dump holds the game's code: keep it out of the repository.
"""
from __future__ import annotations

import ctypes
import struct
import sys
from ctypes import wintypes

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
LIST_MODULES_ALL = 0x03
CHUNK = 0x10000

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
k32.OpenProcess.restype = wintypes.HANDLE
k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
psapi.EnumProcessModulesEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p), wintypes.DWORD,
                                       ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]


class ModuleInfo(ctypes.Structure):
    _fields_ = [("base", ctypes.c_void_p), ("size", wintypes.DWORD), ("entry", ctypes.c_void_p)]


psapi.GetModuleInformation.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.POINTER(ModuleInfo), wintypes.DWORD]


def read(handle, address: int, size: int) -> bytes | None:
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t()
    if k32.ReadProcessMemory(handle, ctypes.c_void_p(address), buf, size, ctypes.byref(got)) and got.value == size:
        return buf.raw
    return None


def main() -> int:
    pid, out = int(sys.argv[1]), sys.argv[2]
    handle = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        print(f"can't open process {pid} for reading (error {ctypes.get_last_error()}; elevated?)", file=sys.stderr)
        return 1

    first = ctypes.c_void_p()
    needed = wintypes.DWORD()
    if not psapi.EnumProcessModulesEx(handle, ctypes.byref(first), ctypes.sizeof(first), ctypes.byref(needed), LIST_MODULES_ALL):
        print(f"can't list modules (error {ctypes.get_last_error()})", file=sys.stderr)
        return 1
    info = ModuleInfo()
    psapi.GetModuleInformation(handle, first, ctypes.byref(info), ctypes.sizeof(info))
    base, size = info.base, info.size

    image = bytearray(size)
    unreadable = 0
    for offset in range(0, size, CHUNK):
        n = min(CHUNK, size - offset)
        chunk = read(handle, base + offset, n)
        if chunk is None:  # read page by page so one guard page doesn't blank the whole chunk
            for page in range(offset, offset + n, 0x1000):
                bytes_ = read(handle, base + page, 0x1000)
                if bytes_ is None:
                    unreadable += 1
                else:
                    image[page:page + 0x1000] = bytes_
        else:
            image[offset:offset + n] = chunk

    pe = struct.unpack_from("<I", image, 0x3C)[0]
    count = struct.unpack_from("<H", image, pe + 6)[0]
    optional_size = struct.unpack_from("<H", image, pe + 20)[0]
    table = pe + 24 + optional_size
    for i in range(count):
        o = table + 40 * i
        virtual_size, virtual_address = struct.unpack_from("<II", image, o + 8)
        struct.pack_into("<II", image, o + 16, virtual_size, virtual_address)  # SizeOfRawData, PointerToRawData
    struct.pack_into("<I", image, pe + 24 + 36, 0x1000)  # FileAlignment = SectionAlignment's usual value

    with open(out, "wb") as f:
        f.write(image)
    print(f"0x{base:X} +0x{size:X}: {count} sections, {unreadable} unreadable page(s) written as zeros -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
