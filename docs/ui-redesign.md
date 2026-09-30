# Native UI redesign completion notes

This implements the three-page design and screenshot audit for the current native Windows app. The legacy Python app is maintained separately.

## Implemented

| Audit area | Result |
| --- | --- |
| Navigation and device panel | Equalizer, Presets, Settings; compact device/firmware header; refresh next to device identity; no separate Device or About page. |
| Control styling | Rounded inputs and dropdowns, correctly separated dropdown arrows, right-aligned switches, accent selection and primary actions, dark/light palettes. |
| EQ preset identity | Actual preset selector and edited indicator; selection stays stable while editing; Save as preset near the picker. |
| Chart | One combined response and a flat 0 dB target by default, draggable numbered markers, no persistent selected-band guide; optional per-band responses in Settings; imported targets remain available; chart grows with window height. |
| EQ bands | All eight bands and the complete editor fit at 1180×780; no nested editor scrolling; narrow windows use a single page scroll. |
| Numeric formatting | Whole-Hz frequency grouping, one decimal for dB, concise Q, no negative zero; very small supported Q retains sufficient precision. |
| Gain information | Grouped pre-gain/global gain, colored estimated headroom, recommended pre-gain, visible auto-protection state. |
| Main actions | Undo/Redo/Reset in header; footer Discard edits, Apply changes, Save to device and an information button explaining saving; unavailable write actions explain why. |
| Device state | Friendly local/applied/save-command states; clickable active EQ identifier explains EQ 9; failures retain edits; raw persistence evidence and capability limitations remain in diagnostics. |
| Preset page | Centered list, active device EQ accent row when connected, local rows with filter count, Open and direct Apply, Import/Export/primary New preset, empty state. |
| Preset management | Rename, duplicate, export, local delete in each row's secondary menu; opening is local, applying is explicit. |
| Settings | Appearance/Behavior/EQ/Advanced in 2×2 groups, one column in narrow windows; proper theme selector and nine functional switches. |
| About and diagnostics | Compact version section, GitHub and release-page links, diagnostics, open/export logs; no duplicate device-details card. |
| Feedback | Routine action feedback disappears after five seconds; actionable errors use a dismissible banner. |
| Small windows | Navigation icons fit within collapsed sidebar; device PNG replaces the M and brand text; selected-band editor moves below the table; footer remains available. |

## Requirements dependent on further device or measurement evidence

- **Hardware EQ bypass:** the UI provides a clearly labeled local Show EQ curve control. It does not imply that hiding the curve bypasses the DAC. No unsupported bypass command was introduced.
- **Inactive device slots:** the active configuration is shown as `EQ <selector> — <name or Custom>`. Additional slot rows/writes are not invented. Local presets remain fully manageable.
- **Verified permanent saves:** Apply readback verifies active values. Save-command completion remains distinct from a verified power-cycle persistence test. The app does not show an unsupported permanent-save guarantee.
- **Acoustic targets:** imported/saved EQ shapes can be compared independently. Built-in acoustic targets, measured response, and automatic acoustic matching require compatible measurement data and a defined basis.
- **Update button:** opens the repository's releases page. It does not claim to query, compare, download, or install a newer version.

## Validation

- Release build and locked dependency restore.
- Default software-only suite, including actual WPF layout, dropdown spacing, switch templates, eight-band visibility, numeric input rendering, preset identity, protocol/failure handling, and persistence checks.
- Separate published-process workspace write/read smoke: import/export round trips, two local presets, remembered settings, edited bands, target, discard behavior, and disabled hardware actions without a connection.
- Visual inspection of normal dark/light EQ, populated and empty Presets, Settings, and narrow layouts.
- Self-contained Windows x64 publish and hardware-free apphost launch.
- Windows runner compatibility: canonical temporary paths, SDK discovery for system/user installations, absolute paths for child-process fixtures, and safe disposal when the optional legacy USB library fails to initialize.

No physical write suite or power-cycle verification was run for this redesign. No GitHub release is created by this change.
