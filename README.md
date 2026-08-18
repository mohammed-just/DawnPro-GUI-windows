# DawnPro-GUI Windows / DAWN PRO2

Cross-platform Python desktop controller for Moondrop Dawn Pro devices, with first-class support for the **Moondrop DAWN PRO2** HID interface.

This version is based on the original [shaypower/DawnPro-GUI](https://github.com/shaypower/DawnPro-GUI) project, which controlled the original Moondrop Dawn Pro through USB control transfers. This fork keeps that original backend and adds a new DAWN PRO2 backend modeled after Moondrop's official [Custom EQ web app](https://app.moondroplab.com/).

## Supported Devices

| Device | Backend | Status |
| --- | --- | --- |
| Moondrop DAWN PRO2 | HID `VID=0x35D8`, `PID=0x011D` | Supported |
| Original Moondrop Dawn Pro | PyUSB control transfers | Preserved from upstream |

The app now prefers the DAWN PRO2 HID backend when that device is connected, then falls back to the original Dawn Pro USB backend.

## DAWN PRO2 Features

- Read firmware version
- Read and set active EQ index
- Read and set pre-gain
- Read and set global gain
- Read all 8 PEQ bands
- Edit PEQ frequency, Q, gain, filter type, and enabled state
- Generate PEQ coefficients compatible with Moondrop Custom EQ
- Apply PEQ coefficients to the device
- Import `.txt` EQ presets compatible with Moondrop Custom EQ and AutoEQ/Equalizer APO
- Save EQ settings to flash
- Save gain offsets to flash
- Diagnostic window for HID and USB device enumeration

Firmware-upgrade command constants are documented in code, but firmware flashing is intentionally not exposed in the GUI yet.

## Install

### Windows

Install Python dependencies:

```powershell
pip install -r requirements.txt
```

Run the app:

```powershell
python main.py
```

DAWN PRO2 uses HID, so it should work without replacing the audio driver with Zadig. Zadig/WinUSB is only relevant if you want to access the original Dawn Pro through PyUSB on Windows.

### WSL / Linux

Install system packages:

```sh
sudo apt update
sudo apt install -y python3-tk python3-hid python3-usb usbutils libhidapi-hidraw0 libhidapi-libusb0
```

Add udev rules:

```sh
sudo tee /etc/udev/rules.d/99-dawn-pro.rules >/dev/null <<'EOF'
SUBSYSTEM=="usb", ATTRS{idVendor}=="2fc6", MODE="0666"
SUBSYSTEM=="usb", ATTRS{idVendor}=="35d8", ATTRS{idProduct}=="011d", MODE="0666", TAG+="uaccess"
KERNEL=="hidraw*", ATTRS{idVendor}=="35d8", ATTRS{idProduct}=="011d", MODE="0666", TAG+="uaccess"
EOF
sudo udevadm control --reload-rules
sudo udevadm trigger
```

For WSL, attach the USB device from Windows with `usbipd-win`, then verify:

```sh
lsusb | grep -i '35d8:011d'
```

Run:

```sh
python3 main.py
```

## Configuration

The app uses a platform-specific config path:

- Windows: `%APPDATA%\dawnpro\config.json`
- Linux / WSL: `~/.config/dawnpro/config.json`

DAWN PRO2 defaults:

```json
{
  "dawn_pro2_settings": {
    "DEFAULT_EQ_INDEX": 0,
    "DEFAULT_PRE_GAIN": 0.0,
    "DEFAULT_GLOBAL_GAIN": 0.0
  }
}
```

## Testing

Run the test suite:

```sh
pytest
```

Current coverage focuses on:

- backward-compatible config loading
- DAWN PRO2 HID packet layout
- fixed-point gain encoding and decoding
- PEQ read parsing
- PEQ coefficient generation
- backend selection between DAWN PRO2 and original Dawn Pro

## EQ Preset Import

On the DAWN PRO2 screen, click **Import EQ File** and choose a `.txt` preset in
the same format accepted by Moondrop Custom EQ:

```text
Preamp: -5.0 dB
Filter 1: ON LSQ Fc 25 Hz Gain 6.0 dB Q 0.710
Filter 2: ON PK Fc 160 Hz Gain -3.0 dB Q 0.550
```

The importer supports `PK`, `LS`/`LSQ`, `HS`/`HSQ`, `LP`/`LPQ`, and `HP`/`HPQ`
filters, plus `OFF` bands and an optional preamp. It validates the entire file
before applying it. Importing changes the live device state; click **Save EQ To
Flash** separately to make the EQ persistent.

## Planned Future Features

- Firmware update workflow with checksum validation and recovery warnings
- Export EQ presets compatible with Moondrop Custom EQ
- Batch PEQ editing and preset comparison
- Realtime graph preview for PEQ filters
- Safer WSL setup helper for `usbipd-win`
- Packaged Windows release with a launcher
- More original Dawn Pro regression tests against the legacy USB backend

## Credits

- Original project: [shaypower/DawnPro-GUI](https://github.com/shaypower/DawnPro-GUI)
- DAWN PRO2 protocol reference: [Moondrop Custom EQ](https://app.moondroplab.com/)
