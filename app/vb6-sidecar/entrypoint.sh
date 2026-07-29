#!/bin/sh
set -e
if [ -f /runtime/msvbvm60.dll ]; then
    cp -f /runtime/msvbvm60.dll "$WINEPREFIX/drive_c/windows/system32/msvbvm60.dll" || true
    wine regsvr32 /s "C:\\windows\\system32\\msvbvm60.dll" || true
fi
if [ -f /runtime/oleaut32.dll ]; then
    cp -f /runtime/oleaut32.dll "$WINEPREFIX/drive_c/windows/system32/oleaut32.dll" || true
fi
if [ -f /runtime/stdole2.tlb ]; then
    cp -f /runtime/stdole2.tlb "$WINEPREFIX/drive_c/windows/system32/stdole2.tlb" || true
fi
exec python3 -m vb6_sidecar.server
