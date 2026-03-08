# TODO

## Current Status

### Completed
- Port the GUI from GTK to Tkinter for Windows.
- Make config and log paths portable.
- Replace Linux-only dependencies with Windows-compatible ones.
- Install dependencies and verify the app runs.
- Add Dawn Pro 2 HID backend support.
- Update the README for Windows and Dawn Pro 2 support.

### Remaining
- Add PEQ editing and write-to-flash support for Dawn Pro 2.
- Add stable preset naming or mapping if it can be confirmed from the vendor dump.
- Refine the Dawn Pro 2 documentation and UI wording.

## Notes
- The original Dawn Pro still uses the legacy PyUSB path.
- Dawn Pro 2 now uses the HID control interface at VID `0x35D8`, PID `0x011D`.
- The current Dawn Pro 2 UI supports firmware display, EQ index, pre-gain, global gain, and read-only PEQ band listing.