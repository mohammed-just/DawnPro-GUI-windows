# DawnPro-Utils
DawnPro-Utils is a tool used to control the Moondrop Dawn Pro AMP/DAC.

![screenshot](preview.png)

## Features

- Change the LED status (on, temp-off, off)
- Set the gain (low, high)
- Configure the filters:
    - Fast-roll-off-low-latency
    - Fast-roll-off-phase-compensated
    - Slow-roll-off-low-latency
    - Slow-roll-off-phase-compensated
    - Non-oversampling
- Adjust the volume
- Fully configurable through JSON configuration file

## Requirements

- Python 3.10 or higher
- `pyusb`
- On Windows: `libusb-package`
- For Dawn Pro 2 HID control: `hidapi`
- Tkinter, which is bundled with standard Python on Windows

## Installation

### From AUR (Arch Linux)

The package is available on the Arch User Repository (AUR). You can install it using your preferred AUR helper:

```sh
# Using yay
yay -S dawnpro-gui

# Using paru
paru -S dawnpro-gui
```

Or build it manually:
```sh
git clone https://aur.archlinux.org/dawnpro-gui.git
cd dawnpro-gui
makepkg -si
```

### Manual Installation

Install the Python packages:

```sh
pip install -r requirements.txt
```

On Windows, the application uses Tkinter for the GUI and `libusb-package` to provide a libusb backend for PyUSB.
For Dawn Pro 2, the application also supports the HID control interface exposed as `VID=0x35D8`, `PID=0x011D`, which works through `hidapi` and does not require replacing the audio driver.

## Setup

Add the following rule to your udev rules (you may need to adjust the rule name based on existing rules in `/etc/udev/rules.d/`):

```sh
echo 'SUBSYSTEM=="usb", ATTRS{idVendor}=="2fc6", MODE="0666"' | sudo tee /etc/udev/rules.d/99-dawn-pro.rules
```

Then run:

```sh
sudo udevadm control --reload-rules
sudo udevadm trigger
```

## Configuration

The application uses a platform-specific configuration file:

- Linux: `~/.config/dawnpro/config.json`
- Windows: `%APPDATA%\dawnpro\config.json`

If the file does not exist, the application uses default settings.

### Setting Up Configuration

1. Create the configuration directory:
```sh
mkdir -p ~/.config/dawnpro
```

2. Copy the default configuration:
```sh
cp config.json ~/.config/dawnpro/config.json
```

On Windows, copy `config.json` to `%APPDATA%\dawnpro\config.json` instead.

### Configuration Sections

The configuration file is divided into several sections:

1. `device_constants`: USB communication constants
   ```json
   "device_constants": {
       "BM_REQUEST_TYPE_OUT": 67,
       "BM_REQUEST_TYPE_IN": 195,
       "B_REQUEST": 160,
       "B_REQUEST_GET": 161,
       "W_VALUE": 0,
       "W_INDEX": 2464,
       "VOLUME_REFRESH_DATA": [192, 165, 162],
       "DATA_LENGTH": 7,
       "LED_STATUS_ENABLED": 0,
       "LED_STATUS_TEMP_OFF": 1,
       "LED_STATUS_OFF": 2
   }
   ```

2. `device_identifiers`: Device vendor and product IDs
   ```json
   "device_identifiers": {
       "MOONDROP_VID": 12230,
       "DAWN_PRO_PID": 61546,
       "ADDITIONAL_DEVICE_IDS": [
           {
               "name": "Moondrop Dawn Pro 2",
               "vendor_id": 1507,
               "product_id": 1865
           }
       ],
       "VOLUME_MAX": 0,
       "VOLUME_MIN": 112
   }
   ```

3. `default_settings`: Default values for device settings
   ```json
   "default_settings": {
       "DEFAULT_VOLUME": 50,
       "DEFAULT_LED_STATUS": "On",
       "DEFAULT_GAIN": "Low",
       "DEFAULT_FILTER": "Fast Roll-Off Low Latency"
   }
   ```

4. `dawn_pro2_settings`: Default values for Dawn Pro 2 HID controls
    ```json
    "dawn_pro2_settings": {
         "DEFAULT_EQ_INDEX": 0,
         "DEFAULT_PRE_GAIN": 0.0,
         "DEFAULT_GLOBAL_GAIN": 0.0
    }
    ```

5. `ui_metrics`: Window size and UI element spacing
   ```json
   "ui_metrics": {
       "WINDOW_WIDTH": 400,
       "WINDOW_HEIGHT": 300,
       "MARGIN_TOP": 10,
       "MARGIN_BOTTOM": 20,
       "MARGIN_START": 10,
       "MARGIN_END": 10,
       "SPACING": 10
   }
   ```

6. `logging`: Logging configuration
   ```json
    "logging": {
        "LOG_LEVEL": "INFO",
        "LOG_FORMAT": "%(asctime)s - %(levelname)s - %(message)s",
        "LOG_FILE": "~/.config/dawnpro/dawnpro.log"
    }
   ```

### Example Custom Configuration

Here's an example of a custom configuration that changes some default values:

```json
{
    "default_settings": {
        "DEFAULT_VOLUME": 75,
        "DEFAULT_LED_STATUS": "Off",
        "DEFAULT_GAIN": "High",
        "DEFAULT_FILTER": "Fast Roll-Off Phase Compensated"
    },
    "ui_metrics": {
        "WINDOW_WIDTH": 500,
        "WINDOW_HEIGHT": 400,
        "SPACING": 15
    },
    "logging": {
        "LOG_LEVEL": "DEBUG",
        "LOG_FILE": "~/.config/dawnpro/debug.log"
    }
}
```

## Usage

Ensure the DAC/AMP is plugged in before running the script.

To run the tool, execute the following command:

```sh
python main.py
```

On Windows, if the device is not found, install a WinUSB-compatible driver for the DAC with Zadig so PyUSB can access it through libusb.

The app currently recognizes both the original Dawn Pro (`VID=0x2FC6`, `PID=0xF06A`) and Dawn Pro 2 (`VID=0x05E3`, `PID=0x0749`).
For Dawn Pro 2, the control path now prefers the separate HID interface (`VID=0x35D8`, `PID=0x011D`) and can read/write firmware version, active EQ preset, pre gain, and global gain.

## Acknowledgments
Inspired by:

"mdrop" by frahz: https://github.com/frahz/mdrop/
