from __future__ import annotations

import logging
import os
import sys
import tkinter as tk
from tkinter import messagebox, ttk

from device.config import AppConfig, get_default_config_path, get_default_log_path
from device.dawnpro2_hid import DawnPro2Hid
from device.moondrop import Moondrop


LED_OPTIONS = ["On", "Temporarily Off", "Off"]
GAIN_OPTIONS = ["Low", "High"]
FILTER_OPTIONS = [
    "Fast Roll-Off Low Latency",
    "Fast Roll-Off Phase Compensated",
    "Slow Roll-Off Low Latency",
    "Slow Roll-Off Phase Compensated",
    "Non-Oversampling",
]


def setup_logging(config: AppConfig) -> None:
    """Set up logging configuration."""
    log_config = config.logging
    handlers = [logging.StreamHandler()]

    log_file = log_config.LOG_FILE or str(get_default_log_path())
    log_file_path = os.path.expanduser(log_file)
    log_dir = os.path.dirname(log_file_path)
    if log_dir:
        os.makedirs(log_dir, exist_ok=True)
    handlers.append(logging.FileHandler(log_file_path))

    logging.basicConfig(
        level=getattr(logging, log_config.LOG_LEVEL),
        format=log_config.LOG_FORMAT,
        handlers=handlers
    )


def show_error_dialog(message: str) -> None:
    """Display an error dialog with the given message."""
    messagebox.showerror("Moondrop Dawn Pro Control", message)


def show_success_dialog(message: str) -> None:
    """Display a success dialog with the given message."""
    messagebox.showinfo("Moondrop Dawn Pro Control", message)


def load_config() -> AppConfig:
    """Load application configuration."""
    return AppConfig.load_from_file(str(get_default_config_path()))


class ModernGUI:
    """Main Tkinter window for the Moondrop Dawn Pro Control application."""

    def __init__(self, root: tk.Tk, config: AppConfig, moondrop: Moondrop) -> None:
        self.root = root
        self.config = config
        self.moondrop = moondrop
        self.config_path = get_default_config_path()
        self.is_syncing = False

        self.root.title("Moondrop Dawn Pro Control")
        self.root.geometry(
            f"{config.ui_metrics.WINDOW_WIDTH}x{config.ui_metrics.WINDOW_HEIGHT}"
        )
        self.root.minsize(360, 320)

        self.volume_var = tk.IntVar(value=self.config.default_settings.DEFAULT_VOLUME)
        self.led_var = tk.StringVar(value=self.config.default_settings.DEFAULT_LED_STATUS)
        self.gain_var = tk.StringVar(value=self.config.default_settings.DEFAULT_GAIN)
        self.filter_var = tk.StringVar(value=self.config.default_settings.DEFAULT_FILTER)
        self.status_var = tk.StringVar(value="Ready")

        self._build_ui()

        if self.config_path.exists():
            self.apply_saved_settings()
        self.refresh_state()

    def _build_ui(self) -> None:
        padding = self.config.ui_metrics.MARGIN_TOP

        frame = ttk.Frame(self.root, padding=padding)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(0, weight=1)

        title = ttk.Label(frame, text="Moondrop Dawn Pro Control", font=("Segoe UI", 14, "bold"))
        title.grid(row=0, column=0, sticky="w", pady=(0, 10))

        self.volume_label = ttk.Label(frame, text=f"Volume: {self.volume_var.get()}")
        self.volume_label.grid(row=1, column=0, sticky="w")

        self.volume_scale = ttk.Scale(
            frame,
            from_=0,
            to=60,
            orient="horizontal",
            command=self.on_volume_changed,
        )
        self.volume_scale.set(self.volume_var.get())
        self.volume_scale.grid(row=2, column=0, sticky="ew", pady=(4, 12))

        self.led_label = ttk.Label(frame, text=f"LED: {self.led_var.get()}")
        self.led_label.grid(row=3, column=0, sticky="w")
        self.led_combo = ttk.Combobox(frame, values=LED_OPTIONS, state="readonly", textvariable=self.led_var)
        self.led_combo.grid(row=4, column=0, sticky="ew", pady=(4, 12))
        self.led_combo.bind("<<ComboboxSelected>>", self.on_led_changed)

        self.gain_label = ttk.Label(frame, text=f"Gain: {self.gain_var.get()}")
        self.gain_label.grid(row=5, column=0, sticky="w")
        self.gain_combo = ttk.Combobox(frame, values=GAIN_OPTIONS, state="readonly", textvariable=self.gain_var)
        self.gain_combo.grid(row=6, column=0, sticky="ew", pady=(4, 12))
        self.gain_combo.bind("<<ComboboxSelected>>", self.on_gain_changed)

        self.filter_label = ttk.Label(frame, text=f"Filter: {self.filter_var.get()}")
        self.filter_label.grid(row=7, column=0, sticky="w")
        self.filter_combo = ttk.Combobox(frame, values=FILTER_OPTIONS, state="readonly", textvariable=self.filter_var)
        self.filter_combo.grid(row=8, column=0, sticky="ew", pady=(4, 12))
        self.filter_combo.bind("<<ComboboxSelected>>", self.on_filter_changed)

        button_frame = ttk.Frame(frame)
        button_frame.grid(row=9, column=0, sticky="ew", pady=(6, 8))
        button_frame.columnconfigure((0, 1), weight=1)

        refresh_button = ttk.Button(button_frame, text="Refresh", command=self.refresh_state)
        refresh_button.grid(row=0, column=0, sticky="ew", padx=(0, 6))

        save_button = ttk.Button(button_frame, text="Save Settings", command=self.save_settings)
        save_button.grid(row=0, column=1, sticky="ew", padx=(6, 0))

        status_label = ttk.Label(frame, textvariable=self.status_var, foreground="#1f4e79")
        status_label.grid(row=10, column=0, sticky="w", pady=(6, 0))

    def set_status(self, message: str) -> None:
        self.status_var.set(message)
        logging.info(message)

    def on_volume_changed(self, value: str) -> None:
        if self.is_syncing:
            return

        volume = int(float(value))
        self.volume_var.set(volume)
        self.volume_label.config(text=f"Volume: {volume}")
        if not self.moondrop.set_volume(volume):
            show_error_dialog(f"Failed to set volume to {volume}")
            logging.error(f"Failed to set volume to {volume}")
            return
        self.set_status(f"Volume set to {volume}")

    def on_led_changed(self, _event: tk.Event[tk.Misc]) -> None:
        if self.is_syncing:
            return

        led_status = self.led_var.get()
        self.led_label.config(text=f"LED: {led_status}")
        if not self.moondrop.set_led_status(led_status):
            show_error_dialog(f"Failed to set LED status to {led_status}")
            logging.error(f"Failed to set LED status to {led_status}")
            return
        self.set_status(f"LED status set to {led_status}")

    def on_gain_changed(self, _event: tk.Event[tk.Misc]) -> None:
        if self.is_syncing:
            return

        gain = self.gain_var.get()
        self.gain_label.config(text=f"Gain: {gain}")
        if not self.moondrop.set_gain(gain):
            show_error_dialog(f"Failed to set gain to {gain}")
            logging.error(f"Failed to set gain to {gain}")
            return
        self.set_status(f"Gain set to {gain}")

    def on_filter_changed(self, _event: tk.Event[tk.Misc]) -> None:
        if self.is_syncing:
            return

        filter_type = self.filter_var.get()
        self.filter_label.config(text=f"Filter: {filter_type}")
        if not self.moondrop.set_filter(filter_type):
            show_error_dialog(f"Failed to set filter to {filter_type}")
            logging.error(f"Failed to set filter to {filter_type}")
            return
        self.set_status(f"Filter set to {filter_type}")

    def apply_saved_settings(self) -> None:
        try:
            volume = self.config.default_settings.DEFAULT_VOLUME
            led_status = self.config.default_settings.DEFAULT_LED_STATUS
            gain = self.config.default_settings.DEFAULT_GAIN
            filter_type = self.config.default_settings.DEFAULT_FILTER

            self.moondrop.set_volume(volume)
            self.moondrop.set_led_status(led_status)
            self.moondrop.set_gain(gain)
            self.moondrop.set_filter(filter_type)
            self.set_status("Applied saved settings")
        except Exception as error:
            logging.warning(f"Failed to apply some saved settings: {error}")

    def refresh_state(self) -> None:
        current_gain = self.moondrop.get_gain()
        current_led = self.moondrop.get_current_led_status()
        current_volume = self.moondrop.get_current_volume()
        current_filter = self.moondrop.get_filter()

        self.is_syncing = True
        try:
            if current_volume is not None:
                self.volume_var.set(current_volume)
                self.volume_scale.set(current_volume)
                self.volume_label.config(text=f"Volume: {current_volume}")

            if current_led:
                self.led_var.set(current_led)
                self.led_label.config(text=f"LED: {current_led}")

            if current_gain:
                self.gain_var.set(current_gain)
                self.gain_label.config(text=f"Gain: {current_gain}")

            if current_filter:
                self.filter_var.set(current_filter)
                self.filter_label.config(text=f"Filter: {current_filter}")
        finally:
            self.is_syncing = False

        self.set_status("Device state refreshed")

    def save_settings(self) -> None:
        try:
            self.config.default_settings.DEFAULT_VOLUME = self.volume_var.get()
            self.config.default_settings.DEFAULT_LED_STATUS = self.led_var.get()
            self.config.default_settings.DEFAULT_GAIN = self.gain_var.get()
            self.config.default_settings.DEFAULT_FILTER = self.filter_var.get()
            self.config.save_to_file(str(self.config_path))
            show_success_dialog(f"Settings saved to {self.config_path}")
            self.set_status("Settings saved")
        except Exception as error:
            error_message = f"Failed to save settings: {error}"
            show_error_dialog(error_message)
            logging.error(error_message)


class DawnPro2GUI:
    """Tkinter UI for the Dawn Pro 2 HID backend."""

    def __init__(self, root: tk.Tk, config: AppConfig, device: DawnPro2Hid) -> None:
        self.root = root
        self.config = config
        self.device = device
        self.config_path = get_default_config_path()
        self.is_syncing = False

        self.root.title('Moondrop Dawn Pro 2 Control')
        self.root.geometry('520x520')
        self.root.minsize(460, 480)

        self.firmware_var = tk.StringVar(value='Unknown')
        self.eq_index_var = tk.IntVar(value=self.config.dawn_pro2_settings.DEFAULT_EQ_INDEX)
        self.pre_gain_var = tk.DoubleVar(value=self.config.dawn_pro2_settings.DEFAULT_PRE_GAIN)
        self.global_gain_var = tk.DoubleVar(value=self.config.dawn_pro2_settings.DEFAULT_GLOBAL_GAIN)
        self.status_var = tk.StringVar(value='Ready')

        self._build_ui()
        self.refresh_state()

    def _build_ui(self) -> None:
        frame = ttk.Frame(self.root, padding=12)
        frame.pack(fill='both', expand=True)
        frame.columnconfigure(0, weight=1)

        ttk.Label(frame, text='Moondrop Dawn Pro 2', font=('Segoe UI', 14, 'bold')).grid(
            row=0, column=0, sticky='w', pady=(0, 10)
        )
        ttk.Label(frame, text='HID control backend detected from device dump').grid(
            row=1, column=0, sticky='w', pady=(0, 12)
        )

        ttk.Label(frame, text='Firmware version').grid(row=2, column=0, sticky='w')
        ttk.Label(frame, textvariable=self.firmware_var).grid(row=3, column=0, sticky='w', pady=(4, 12))

        ttk.Label(frame, text='Active EQ preset/index').grid(row=4, column=0, sticky='w')
        eq_frame = ttk.Frame(frame)
        eq_frame.grid(row=5, column=0, sticky='ew', pady=(4, 12))
        eq_frame.columnconfigure(0, weight=1)
        self.eq_spinbox = ttk.Spinbox(eq_frame, from_=0, to=15, textvariable=self.eq_index_var)
        self.eq_spinbox.grid(row=0, column=0, sticky='ew', padx=(0, 8))
        ttk.Button(eq_frame, text='Apply EQ', command=self.apply_eq_index).grid(row=0, column=1)

        ttk.Label(frame, text='Pre Gain (dB)').grid(row=6, column=0, sticky='w')
        pre_gain_frame = ttk.Frame(frame)
        pre_gain_frame.grid(row=7, column=0, sticky='ew', pady=(4, 12))
        pre_gain_frame.columnconfigure(0, weight=1)
        self.pre_gain_scale = ttk.Scale(
            pre_gain_frame,
            from_=-18,
            to=12,
            orient='horizontal',
            command=self.on_pre_gain_slide,
        )
        self.pre_gain_scale.grid(row=0, column=0, sticky='ew', padx=(0, 8))
        self.pre_gain_value_label = ttk.Label(pre_gain_frame, text='0.00 dB', width=10)
        self.pre_gain_value_label.grid(row=0, column=1)
        ttk.Button(pre_gain_frame, text='Apply', command=self.apply_pre_gain).grid(row=0, column=2, padx=(8, 0))

        ttk.Label(frame, text='Global Gain (dB)').grid(row=8, column=0, sticky='w')
        global_gain_frame = ttk.Frame(frame)
        global_gain_frame.grid(row=9, column=0, sticky='ew', pady=(4, 12))
        global_gain_frame.columnconfigure(0, weight=1)
        self.global_gain_scale = ttk.Scale(
            global_gain_frame,
            from_=-18,
            to=12,
            orient='horizontal',
            command=self.on_global_gain_slide,
        )
        self.global_gain_scale.grid(row=0, column=0, sticky='ew', padx=(0, 8))
        self.global_gain_value_label = ttk.Label(global_gain_frame, text='0.00 dB', width=10)
        self.global_gain_value_label.grid(row=0, column=1)
        ttk.Button(global_gain_frame, text='Apply', command=self.apply_global_gain).grid(row=0, column=2, padx=(8, 0))

        ttk.Label(frame, text='Current PEQ bands').grid(row=10, column=0, sticky='w')
        self.peq_text = tk.Text(frame, height=10, width=70, wrap='none')
        self.peq_text.grid(row=11, column=0, sticky='nsew', pady=(4, 12))
        frame.rowconfigure(11, weight=1)

        button_frame = ttk.Frame(frame)
        button_frame.grid(row=12, column=0, sticky='ew', pady=(0, 8))
        button_frame.columnconfigure((0, 1), weight=1)
        ttk.Button(button_frame, text='Refresh', command=self.refresh_state).grid(row=0, column=0, sticky='ew', padx=(0, 6))
        ttk.Button(button_frame, text='Save Defaults', command=self.save_settings).grid(row=0, column=1, sticky='ew', padx=(6, 0))

        ttk.Label(frame, textvariable=self.status_var, foreground='#1f4e79').grid(row=13, column=0, sticky='w')

    def set_status(self, message: str) -> None:
        self.status_var.set(message)
        logging.info(message)

    def on_pre_gain_slide(self, value: str) -> None:
        self.pre_gain_var.set(round(float(value), 2))
        self.pre_gain_value_label.config(text=f'{self.pre_gain_var.get():.2f} dB')

    def on_global_gain_slide(self, value: str) -> None:
        self.global_gain_var.set(round(float(value), 2))
        self.global_gain_value_label.config(text=f'{self.global_gain_var.get():.2f} dB')

    def apply_eq_index(self) -> None:
        try:
            self.device.write_eq_index(self.eq_index_var.get())
            self.set_status(f'Active EQ preset set to {self.eq_index_var.get()}')
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f'Failed to apply EQ index: {error}')

    def apply_pre_gain(self) -> None:
        try:
            self.device.write_pre_gain(self.pre_gain_var.get())
            self.set_status(f'Pre gain set to {self.pre_gain_var.get():.2f} dB')
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f'Failed to apply pre gain: {error}')

    def apply_global_gain(self) -> None:
        try:
            self.device.write_global_gain(self.global_gain_var.get())
            self.set_status(f'Global gain set to {self.global_gain_var.get():.2f} dB')
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f'Failed to apply global gain: {error}')

    def refresh_state(self) -> None:
        try:
            firmware = self.device.read_firmware_version()
            eq_index = self.device.read_eq_index()
            pre_gain = self.device.read_pre_gain()
            global_gain = self.device.read_global_gain()
            bands = self.device.read_all_peq_bands()
        except Exception as error:
            show_error_dialog(f'Failed to refresh Dawn Pro 2 state: {error}')
            return

        self.firmware_var.set(firmware)
        self.eq_index_var.set(eq_index)
        self.pre_gain_var.set(round(pre_gain, 2))
        self.global_gain_var.set(round(global_gain, 2))
        self.pre_gain_scale.set(pre_gain)
        self.global_gain_scale.set(global_gain)
        self.pre_gain_value_label.config(text=f'{pre_gain:.2f} dB')
        self.global_gain_value_label.config(text=f'{global_gain:.2f} dB')

        band_lines = []
        for band in bands:
            band_lines.append(
                f'Band {band.index}: {band.filter_type}, {band.frequency} Hz, Q {band.q:.2f}, Gain {band.gain:.2f} dB, Enabled: {band.enabled}'
            )
        self.peq_text.delete('1.0', tk.END)
        self.peq_text.insert('1.0', '\n'.join(band_lines))

        self.set_status('Dawn Pro 2 state refreshed')

    def save_settings(self) -> None:
        try:
            self.config.dawn_pro2_settings.DEFAULT_EQ_INDEX = self.eq_index_var.get()
            self.config.dawn_pro2_settings.DEFAULT_PRE_GAIN = self.pre_gain_var.get()
            self.config.dawn_pro2_settings.DEFAULT_GLOBAL_GAIN = self.global_gain_var.get()
            self.config.save_to_file(str(self.config_path))
            show_success_dialog(f'Settings saved to {self.config_path}')
            self.set_status('Dawn Pro 2 defaults saved')
        except Exception as error:
            show_error_dialog(f'Failed to save Dawn Pro 2 defaults: {error}')


def main() -> int:
    root = tk.Tk()
    root.withdraw()

    config = load_config()
    setup_logging(config)

    try:
        moondrop = Moondrop(config)
        root.deiconify()
        ModernGUI(root, config, moondrop)
        root.mainloop()
        return 0
    except ValueError as legacy_error:
        logging.info(f'Legacy Dawn Pro backend unavailable: {legacy_error}')

    try:
        dawn_pro2 = DawnPro2Hid(config)
        root.deiconify()
        DawnPro2GUI(root, config, dawn_pro2)
        root.mainloop()
        return 0
    except ValueError as pro2_error:
        show_error_dialog(f'{legacy_error}\n\n{pro2_error}')
        return 1


if __name__ == "__main__":
    sys.exit(main())
