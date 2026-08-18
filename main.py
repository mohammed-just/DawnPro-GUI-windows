from __future__ import annotations

import logging
import os
import sys
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Any, Iterable, List

from device.backends import BackendSelection, select_backend
from device.config import AppConfig, get_default_config_path, get_default_log_path
from device.dawnpro2_hid import DawnPro2Hid, DawnPro2PeqBand
from device.eq_presets import load_eq_preset


LED_OPTIONS = ["On", "Temporarily Off", "Off"]
GAIN_OPTIONS = ["Low", "High"]
LEGACY_FILTER_OPTIONS = [
    "Fast Roll-Off Low Latency",
    "Fast Roll-Off Phase Compensated",
    "Slow Roll-Off Low Latency",
    "Slow Roll-Off Phase Compensated",
    "Non-Oversampling",
]
PRO2_FILTER_OPTIONS = list(DawnPro2Hid.FILTER_LABELS.values())


def setup_logging(config: AppConfig) -> None:
    """Set up logging configuration."""
    log_config = config.logging
    handlers: List[logging.Handler] = [logging.StreamHandler()]
    log_file = log_config.LOG_FILE or str(get_default_log_path())
    log_file_path = os.path.expanduser(log_file)
    log_dir = os.path.dirname(log_file_path)
    if log_dir:
        os.makedirs(log_dir, exist_ok=True)
    handlers.append(logging.FileHandler(log_file_path))

    logging.basicConfig(
        level=getattr(logging, log_config.LOG_LEVEL),
        format=log_config.LOG_FORMAT,
        handlers=handlers,
        force=True,
    )


def show_error_dialog(message: str) -> None:
    messagebox.showerror("Moondrop Dawn Pro Control", message)


def show_success_dialog(message: str) -> None:
    messagebox.showinfo("Moondrop Dawn Pro Control", message)


def load_config() -> AppConfig:
    return AppConfig.load_from_file(str(get_default_config_path()))


def _grid_columns(frame: ttk.Frame, columns: Iterable[int]) -> None:
    for column in columns:
        frame.columnconfigure(column, weight=1)


class LegacyDawnProGUI:
    """Tkinter UI for the original Dawn Pro USB control backend."""

    def __init__(self, root: tk.Tk, config: AppConfig, moondrop: Any) -> None:
        self.root = root
        self.config = config
        self.moondrop = moondrop
        self.config_path = get_default_config_path()
        self.is_syncing = False

        self.volume_var = tk.IntVar(value=self.config.default_settings.DEFAULT_VOLUME)
        self.led_var = tk.StringVar(value=self.config.default_settings.DEFAULT_LED_STATUS)
        self.gain_var = tk.StringVar(value=self.config.default_settings.DEFAULT_GAIN)
        self.filter_var = tk.StringVar(value=self.config.default_settings.DEFAULT_FILTER)
        self.status_var = tk.StringVar(value="Ready")

        self.root.title("Moondrop Dawn Pro Control")
        self.root.geometry(
            f"{config.ui_metrics.WINDOW_WIDTH}x{max(config.ui_metrics.WINDOW_HEIGHT, 340)}"
        )
        self.root.minsize(360, 320)
        self._build_ui()

        if self.config_path.exists():
            self.apply_saved_settings()
        self.refresh_state()

    def _build_ui(self) -> None:
        frame = ttk.Frame(self.root, padding=12)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(0, weight=1)

        ttk.Label(frame, text="Moondrop Dawn Pro", font=("Segoe UI", 14, "bold")).grid(
            row=0, column=0, sticky="w", pady=(0, 10)
        )

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
        self.led_combo = ttk.Combobox(
            frame, values=LED_OPTIONS, state="readonly", textvariable=self.led_var
        )
        self.led_combo.grid(row=4, column=0, sticky="ew", pady=(4, 12))
        self.led_combo.bind("<<ComboboxSelected>>", self.on_led_changed)

        self.gain_label = ttk.Label(frame, text=f"Gain: {self.gain_var.get()}")
        self.gain_label.grid(row=5, column=0, sticky="w")
        self.gain_combo = ttk.Combobox(
            frame, values=GAIN_OPTIONS, state="readonly", textvariable=self.gain_var
        )
        self.gain_combo.grid(row=6, column=0, sticky="ew", pady=(4, 12))
        self.gain_combo.bind("<<ComboboxSelected>>", self.on_gain_changed)

        self.filter_label = ttk.Label(frame, text=f"Filter: {self.filter_var.get()}")
        self.filter_label.grid(row=7, column=0, sticky="w")
        self.filter_combo = ttk.Combobox(
            frame, values=LEGACY_FILTER_OPTIONS, state="readonly", textvariable=self.filter_var
        )
        self.filter_combo.grid(row=8, column=0, sticky="ew", pady=(4, 12))
        self.filter_combo.bind("<<ComboboxSelected>>", self.on_filter_changed)

        button_frame = ttk.Frame(frame)
        button_frame.grid(row=9, column=0, sticky="ew", pady=(6, 8))
        _grid_columns(button_frame, (0, 1))
        ttk.Button(button_frame, text="Refresh", command=self.refresh_state).grid(
            row=0, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(button_frame, text="Save Settings", command=self.save_settings).grid(
            row=0, column=1, sticky="ew", padx=(6, 0)
        )

        ttk.Label(frame, textvariable=self.status_var, foreground="#1f4e79").grid(
            row=10, column=0, sticky="w", pady=(6, 0)
        )

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
            return
        self.set_status(f"Volume set to {volume}")

    def on_led_changed(self, _event: tk.Event[tk.Misc]) -> None:
        if self.is_syncing:
            return
        led_status = self.led_var.get()
        self.led_label.config(text=f"LED: {led_status}")
        if not self.moondrop.set_led_status(led_status):
            show_error_dialog(f"Failed to set LED status to {led_status}")
            return
        self.set_status(f"LED status set to {led_status}")

    def on_gain_changed(self, _event: tk.Event[tk.Misc]) -> None:
        if self.is_syncing:
            return
        gain = self.gain_var.get()
        self.gain_label.config(text=f"Gain: {gain}")
        if not self.moondrop.set_gain(gain):
            show_error_dialog(f"Failed to set gain to {gain}")
            return
        self.set_status(f"Gain set to {gain}")

    def on_filter_changed(self, _event: tk.Event[tk.Misc]) -> None:
        if self.is_syncing:
            return
        filter_type = self.filter_var.get()
        self.filter_label.config(text=f"Filter: {filter_type}")
        if not self.moondrop.set_filter(filter_type):
            show_error_dialog(f"Failed to set filter to {filter_type}")
            return
        self.set_status(f"Filter set to {filter_type}")

    def apply_saved_settings(self) -> None:
        try:
            self.moondrop.set_volume(self.config.default_settings.DEFAULT_VOLUME)
            self.moondrop.set_led_status(self.config.default_settings.DEFAULT_LED_STATUS)
            self.moondrop.set_gain(self.config.default_settings.DEFAULT_GAIN)
            self.moondrop.set_filter(self.config.default_settings.DEFAULT_FILTER)
            self.set_status("Applied saved settings")
        except Exception as error:
            logging.warning("Failed to apply some saved settings: %s", error)

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
            show_error_dialog(f"Failed to save settings: {error}")


class DawnPro2GUI:
    """Tkinter UI for the Dawn Pro 2 HID backend."""

    def __init__(self, root: tk.Tk, config: AppConfig, device: DawnPro2Hid) -> None:
        self.root = root
        self.config = config
        self.device = device
        self.config_path = get_default_config_path()
        self.is_syncing = False
        self.peq_bands: List[DawnPro2PeqBand] = []

        self.firmware_var = tk.StringVar(value="Unknown")
        self.eq_index_var = tk.IntVar(value=self.config.dawn_pro2_settings.DEFAULT_EQ_INDEX)
        self.pre_gain_var = tk.DoubleVar(value=self.config.dawn_pro2_settings.DEFAULT_PRE_GAIN)
        self.global_gain_var = tk.DoubleVar(value=self.config.dawn_pro2_settings.DEFAULT_GLOBAL_GAIN)
        self.status_var = tk.StringVar(value="Ready")

        self.band_index_var = tk.IntVar(value=0)
        self.band_frequency_var = tk.StringVar(value="1000")
        self.band_q_var = tk.StringVar(value="1.00")
        self.band_gain_var = tk.StringVar(value="0.00")
        self.band_filter_var = tk.StringVar(value=DawnPro2Hid.FILTER_LABELS["PEAKING"])
        self.band_enabled_var = tk.BooleanVar(value=True)

        self.root.title("Moondrop DAWN PRO2 Control")
        self.root.geometry("820x680")
        self.root.minsize(760, 620)
        self._build_ui()
        self.refresh_state()

    def _build_ui(self) -> None:
        frame = ttk.Frame(self.root, padding=12)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(3, weight=1)

        ttk.Label(frame, text="Moondrop DAWN PRO2", font=("Segoe UI", 15, "bold")).grid(
            row=0, column=0, sticky="w", pady=(0, 10)
        )

        status_frame = ttk.LabelFrame(frame, text="Device")
        status_frame.grid(row=1, column=0, sticky="ew", pady=(0, 10))
        _grid_columns(status_frame, (1, 3))
        ttk.Label(status_frame, text="Firmware").grid(row=0, column=0, sticky="w", padx=8, pady=6)
        ttk.Label(status_frame, textvariable=self.firmware_var).grid(
            row=0, column=1, sticky="w", padx=8, pady=6
        )
        ttk.Label(status_frame, text="Active EQ").grid(row=0, column=2, sticky="w", padx=8, pady=6)
        eq_frame = ttk.Frame(status_frame)
        eq_frame.grid(row=0, column=3, sticky="ew", padx=8, pady=6)
        eq_frame.columnconfigure(0, weight=1)
        ttk.Spinbox(eq_frame, from_=0, to=15, textvariable=self.eq_index_var, width=6).grid(
            row=0, column=0, sticky="ew"
        )
        ttk.Button(eq_frame, text="Apply", command=self.apply_eq_index).grid(
            row=0, column=1, padx=(8, 0)
        )

        gain_frame = ttk.LabelFrame(frame, text="Gain")
        gain_frame.grid(row=2, column=0, sticky="ew", pady=(0, 10))
        gain_frame.columnconfigure(1, weight=1)
        self._build_gain_row(gain_frame, 0, "Pre Gain", self.pre_gain_var, self.on_pre_gain_slide, self.apply_pre_gain)
        self._build_gain_row(
            gain_frame,
            1,
            "Global Gain",
            self.global_gain_var,
            self.on_global_gain_slide,
            self.apply_global_gain,
        )

        peq_frame = ttk.LabelFrame(frame, text="PEQ")
        peq_frame.grid(row=3, column=0, sticky="nsew", pady=(0, 10))
        peq_frame.columnconfigure(0, weight=2)
        peq_frame.columnconfigure(1, weight=1)
        peq_frame.rowconfigure(0, weight=1)
        self._build_peq_table(peq_frame)
        self._build_peq_editor(peq_frame)

        button_frame = ttk.Frame(frame)
        button_frame.grid(row=4, column=0, sticky="ew")
        _grid_columns(button_frame, (0, 1, 2, 3, 4))
        ttk.Button(button_frame, text="Refresh", command=self.refresh_state).grid(
            row=0, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(button_frame, text="Import EQ File", command=self.import_eq_file).grid(
            row=0, column=1, sticky="ew", padx=6
        )
        ttk.Button(button_frame, text="Save EQ To Flash", command=self.save_eq_to_flash).grid(
            row=0, column=2, sticky="ew", padx=6
        )
        ttk.Button(button_frame, text="Save Gains To Flash", command=self.save_gains_to_flash).grid(
            row=0, column=3, sticky="ew", padx=6
        )
        ttk.Button(button_frame, text="Diagnostics", command=self.show_diagnostics).grid(
            row=0, column=4, sticky="ew", padx=(6, 0)
        )

        ttk.Label(frame, textvariable=self.status_var, foreground="#1f4e79").grid(
            row=5, column=0, sticky="w", pady=(8, 0)
        )

    def _build_gain_row(
        self,
        parent: ttk.LabelFrame,
        row: int,
        label: str,
        variable: tk.DoubleVar,
        slide_command: Any,
        apply_command: Any,
    ) -> None:
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=8, pady=6)
        ttk.Scale(
            parent,
            from_=-18,
            to=12,
            orient="horizontal",
            variable=variable,
            command=slide_command,
        ).grid(row=row, column=1, sticky="ew", padx=8, pady=6)
        value_label = ttk.Label(parent, width=10)
        value_label.grid(row=row, column=2, sticky="e", padx=8, pady=6)
        if row == 0:
            self.pre_gain_value_label = value_label
        else:
            self.global_gain_value_label = value_label
        ttk.Button(parent, text="Apply", command=apply_command).grid(
            row=row, column=3, sticky="ew", padx=8, pady=6
        )

    def _build_peq_table(self, parent: ttk.LabelFrame) -> None:
        columns = ("frequency", "q", "gain", "filter", "enabled")
        self.peq_tree = ttk.Treeview(parent, columns=columns, show="headings", height=8)
        headings = {
            "frequency": "Freq Hz",
            "q": "Q",
            "gain": "Gain dB",
            "filter": "Filter",
            "enabled": "Enabled",
        }
        widths = {"frequency": 80, "q": 70, "gain": 80, "filter": 140, "enabled": 70}
        for column in columns:
            self.peq_tree.heading(column, text=headings[column])
            self.peq_tree.column(column, width=widths[column], anchor="center")
        self.peq_tree.grid(row=0, column=0, sticky="nsew", padx=(8, 6), pady=8)
        self.peq_tree.bind("<<TreeviewSelect>>", self.on_band_selected)

    def _build_peq_editor(self, parent: ttk.LabelFrame) -> None:
        editor = ttk.Frame(parent)
        editor.grid(row=0, column=1, sticky="nsew", padx=(6, 8), pady=8)
        editor.columnconfigure(1, weight=1)

        fields = [
            ("Band", ttk.Spinbox(editor, from_=0, to=7, textvariable=self.band_index_var, width=8)),
            ("Frequency", ttk.Entry(editor, textvariable=self.band_frequency_var)),
            ("Q", ttk.Entry(editor, textvariable=self.band_q_var)),
            ("Gain", ttk.Entry(editor, textvariable=self.band_gain_var)),
            (
                "Filter",
                ttk.Combobox(
                    editor,
                    values=PRO2_FILTER_OPTIONS,
                    state="readonly",
                    textvariable=self.band_filter_var,
                ),
            ),
        ]
        for row, (label, widget) in enumerate(fields):
            ttk.Label(editor, text=label).grid(row=row, column=0, sticky="w", pady=4)
            widget.grid(row=row, column=1, sticky="ew", pady=4)

        ttk.Checkbutton(editor, text="Enabled", variable=self.band_enabled_var).grid(
            row=5, column=0, columnspan=2, sticky="w", pady=(4, 10)
        )
        ttk.Button(editor, text="Load Band", command=self.load_selected_band).grid(
            row=6, column=0, columnspan=2, sticky="ew", pady=3
        )
        ttk.Button(editor, text="Apply Band", command=self.apply_band).grid(
            row=7, column=0, columnspan=2, sticky="ew", pady=3
        )
        ttk.Button(editor, text="Enable Coefficients", command=self.enable_current_band).grid(
            row=8, column=0, columnspan=2, sticky="ew", pady=3
        )
        ttk.Button(editor, text="Save Defaults", command=self.save_settings).grid(
            row=9, column=0, columnspan=2, sticky="ew", pady=(12, 3)
        )

    def set_status(self, message: str) -> None:
        self.status_var.set(message)
        logging.info(message)

    def on_pre_gain_slide(self, value: str) -> None:
        self.pre_gain_var.set(round(float(value), 2))
        self.pre_gain_value_label.config(text=f"{self.pre_gain_var.get():.2f} dB")

    def on_global_gain_slide(self, value: str) -> None:
        self.global_gain_var.set(round(float(value), 2))
        self.global_gain_value_label.config(text=f"{self.global_gain_var.get():.2f} dB")

    def apply_eq_index(self) -> None:
        try:
            self.device.write_eq_index(self.eq_index_var.get())
            self.set_status(f"Active EQ preset set to {self.eq_index_var.get()}")
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f"Failed to apply EQ index: {error}")

    def apply_pre_gain(self) -> None:
        try:
            self.device.write_pre_gain(self.pre_gain_var.get())
            self.set_status(f"Pre gain set to {self.pre_gain_var.get():.2f} dB")
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f"Failed to apply pre gain: {error}")

    def apply_global_gain(self) -> None:
        try:
            self.device.write_global_gain(self.global_gain_var.get())
            self.set_status(f"Global gain set to {self.global_gain_var.get():.2f} dB")
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f"Failed to apply global gain: {error}")

    def refresh_state(self) -> None:
        try:
            firmware = self.device.read_firmware_version()
            eq_index = self.device.read_eq_index()
            pre_gain = self.device.read_pre_gain()
            global_gain = self.device.read_global_gain()
            bands = self.device.read_all_peq_bands()
        except Exception as error:
            show_error_dialog(f"Failed to refresh Dawn Pro 2 state: {error}")
            return

        self.firmware_var.set(firmware or "Unknown")
        self.eq_index_var.set(eq_index)
        self.pre_gain_var.set(round(pre_gain, 2))
        self.global_gain_var.set(round(global_gain, 2))
        self.on_pre_gain_slide(str(pre_gain))
        self.on_global_gain_slide(str(global_gain))
        self.peq_bands = bands
        self._populate_peq_table()
        if bands:
            self.load_band_into_editor(bands[0])
        self.set_status("Dawn Pro 2 state refreshed")

    def _populate_peq_table(self) -> None:
        for item in self.peq_tree.get_children():
            self.peq_tree.delete(item)
        for band in self.peq_bands:
            self.peq_tree.insert(
                "",
                "end",
                iid=str(band.index),
                values=(
                    band.frequency,
                    f"{band.q:.2f}",
                    f"{band.gain:.2f}",
                    DawnPro2Hid.filter_label(band.filter_type),
                    "Yes" if band.enabled else "No",
                ),
            )

    def on_band_selected(self, _event: tk.Event[tk.Misc]) -> None:
        self.load_selected_band()

    def load_selected_band(self) -> None:
        selected = self.peq_tree.selection()
        if not selected:
            return
        index = int(selected[0])
        if index < len(self.peq_bands):
            self.load_band_into_editor(self.peq_bands[index])

    def load_band_into_editor(self, band: DawnPro2PeqBand) -> None:
        self.band_index_var.set(band.index)
        self.band_frequency_var.set(str(band.frequency))
        self.band_q_var.set(f"{band.q:.2f}")
        self.band_gain_var.set(f"{band.gain:.2f}")
        self.band_filter_var.set(DawnPro2Hid.filter_label(band.filter_type))
        self.band_enabled_var.set(band.enabled)

    def get_editor_band(self) -> DawnPro2PeqBand:
        index = self.band_index_var.get()
        frequency = int(float(self.band_frequency_var.get()))
        q_value = float(self.band_q_var.get())
        gain = float(self.band_gain_var.get())
        filter_type = DawnPro2Hid.normalize_filter_type(self.band_filter_var.get())
        return DawnPro2PeqBand(
            index=index,
            frequency=frequency,
            q=q_value,
            gain=gain,
            filter_type=filter_type,
            enabled=self.band_enabled_var.get(),
        )

    def apply_band(self) -> None:
        try:
            band = self.get_editor_band()
            self.device.write_peq_band(band.index, band)
            self.device.enable_peq_band(band.index)
            self.set_status(f"PEQ band {band.index} applied")
            self.refresh_state()
        except Exception as error:
            show_error_dialog(f"Failed to apply PEQ band: {error}")

    def enable_current_band(self) -> None:
        try:
            index = self.band_index_var.get()
            self.device.enable_peq_band(index)
            self.set_status(f"PEQ band {index} coefficients enabled")
        except Exception as error:
            show_error_dialog(f"Failed to enable PEQ band coefficients: {error}")

    def import_eq_file(self) -> None:
        filename = filedialog.askopenfilename(
            parent=self.root,
            title="Import Moondrop EQ preset",
            filetypes=(
                ("EQ text presets", "*.txt"),
                ("All files", "*.*"),
            ),
        )
        if not filename:
            return

        try:
            preset = load_eq_preset(filename)
        except (OSError, ValueError) as error:
            show_error_dialog(f"Could not import EQ preset:\n\n{error}")
            return

        enabled_count = sum(band.enabled for band in preset.bands)
        preamp_summary = (
            f"\nPreamp: {preset.preamp:.2f} dB" if preset.preamp is not None else ""
        )
        should_apply = messagebox.askyesno(
            "Import EQ preset",
            f"Apply {len(preset.bands)} bands from {Path(filename).name}?\n"
            f"Enabled bands: {enabled_count}{preamp_summary}\n\n"
            "This applies the preset to the device now. It will not be saved to "
            "flash until you click Save EQ To Flash.",
            parent=self.root,
        )
        if not should_apply:
            return

        try:
            if preset.preamp is not None:
                self.device.write_pre_gain(preset.preamp)
            self.device.write_all_peq_bands(preset.bands)
            self.set_status(f"Imported EQ preset: {Path(filename).name}")
            self.refresh_state()
            show_success_dialog(
                f"Imported {len(preset.bands)} EQ bands from {Path(filename).name}.\n\n"
                "Use Save EQ To Flash when you are ready to make it persistent."
            )
        except Exception as error:
            show_error_dialog(f"Failed to apply EQ preset to the device:\n\n{error}")

    def save_eq_to_flash(self) -> None:
        try:
            self.device.save_eq_to_flash()
            self.set_status("EQ saved to flash")
        except Exception as error:
            show_error_dialog(f"Failed to save EQ to flash: {error}")

    def save_gains_to_flash(self) -> None:
        try:
            self.device.save_offset_to_flash()
            self.set_status("Gain offsets saved to flash")
        except Exception as error:
            show_error_dialog(f"Failed to save gain offsets to flash: {error}")

    def save_settings(self) -> None:
        try:
            self.config.dawn_pro2_settings.DEFAULT_EQ_INDEX = self.eq_index_var.get()
            self.config.dawn_pro2_settings.DEFAULT_PRE_GAIN = self.pre_gain_var.get()
            self.config.dawn_pro2_settings.DEFAULT_GLOBAL_GAIN = self.global_gain_var.get()
            self.config.save_to_file(str(self.config_path))
            show_success_dialog(f"Settings saved to {self.config_path}")
            self.set_status("Dawn Pro 2 defaults saved")
        except Exception as error:
            show_error_dialog(f"Failed to save Dawn Pro 2 defaults: {error}")

    def show_diagnostics(self) -> None:
        diagnostics = collect_diagnostics()
        logging.info("Diagnostics:\n%s", diagnostics)
        window = tk.Toplevel(self.root)
        window.title("DAWN PRO2 Diagnostics")
        window.geometry("760x420")
        text = tk.Text(window, wrap="none")
        text.pack(fill="both", expand=True)
        text.insert("1.0", diagnostics)
        text.configure(state="disabled")


def collect_diagnostics() -> str:
    lines = ["HID devices:"]
    try:
        for item in DawnPro2Hid.enumerate_devices():
            lines.append(
                "  "
                + ", ".join(
                    [
                        f"vendor=0x{int(item.get('vendor_id', 0)):04X}",
                        f"product=0x{int(item.get('product_id', 0)):04X}",
                        f"usage_page={item.get('usage_page')}",
                        f"usage={item.get('usage')}",
                        f"product_string={item.get('product_string')}",
                        f"manufacturer={item.get('manufacturer_string')}",
                    ]
                )
            )
    except Exception as error:
        lines.append(f"  HID diagnostics unavailable: {error}")

    lines.append("")
    lines.append("USB devices:")
    try:
        import usb.core  # type: ignore

        for device in usb.core.find(find_all=True):
            lines.append(
                f"  vendor=0x{device.idVendor:04X}, product=0x{device.idProduct:04X}, "
                f"bus={getattr(device, 'bus', '?')}, address={getattr(device, 'address', '?')}"
            )
    except Exception as error:
        lines.append(f"  USB diagnostics unavailable: {error}")

    return "\n".join(lines)


def build_gui(root: tk.Tk, config: AppConfig, selection: BackendSelection) -> Any:
    if selection.kind == "dawn_pro2":
        return DawnPro2GUI(root, config, selection.device)
    return LegacyDawnProGUI(root, config, selection.device)


def main() -> int:
    root = tk.Tk()
    root.withdraw()
    root.protocol("WM_DELETE_WINDOW", root.destroy)

    config = load_config()
    setup_logging(config)

    try:
        selection = select_backend(config)
    except ValueError as error:
        show_error_dialog(str(error))
        return 1

    root.deiconify()
    build_gui(root, config, selection)
    logging.info("Started GUI with backend: %s", selection.kind)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
