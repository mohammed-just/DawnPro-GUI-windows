# Roadmap

## Completed

- Ported the GUI from GTK to Tkinter for Windows and WSL/Linux.
- Added platform-specific config and log paths.
- Preserved the original Dawn Pro PyUSB backend.
- Added DAWN PRO2 HID support for `VID=0x35D8`, `PID=0x011D`.
- Added DAWN PRO2 firmware, EQ index, pre-gain, global-gain, and PEQ controls.
- Added Moondrop-compatible PEQ coefficient generation.
- Added save-to-flash actions for EQ and gain offsets.
- Added diagnostics for HID and USB enumeration.
- Added tests for config compatibility, backend selection, HID packet layout, fixed-point gain values, PEQ parsing, and PEQ write packets.

## Planned Future Features

- Firmware update workflow with checksum validation and recovery warnings.
- Import/export EQ presets compatible with Moondrop Custom EQ.
- Batch PEQ editing and preset comparison.
- Realtime graph preview for PEQ filters.
- Safer WSL setup helper for `usbipd-win`.
- Packaged Windows release with a launcher.
- More original Dawn Pro regression tests against the legacy USB backend.

## Notes

- DAWN PRO2 support is based on the HID protocol exposed by Moondrop Custom EQ.
- Firmware flashing is intentionally not exposed yet because a bad flash can brick the device.
