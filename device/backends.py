from dataclasses import dataclass
from typing import Any, List, Type

from device.config import AppConfig
from device.dawnpro2_hid import DawnPro2Hid
from device.moondrop import Moondrop


@dataclass
class BackendSelection:
    """Resolved device backend for the GUI."""

    kind: str
    display_name: str
    device: Any
    errors: List[str]


def select_backend(
    config: AppConfig,
    dawn_pro2_cls: Type[Any] = DawnPro2Hid,
    legacy_cls: Type[Any] = Moondrop,
) -> BackendSelection:
    """Select the best available supported device backend.

    DAWN PRO2 is tried first so its HID interface is preferred over any generic
    USB interface that might appear on composite audio devices.
    """
    errors: List[str] = []

    try:
        device = dawn_pro2_cls(config)
        return BackendSelection("dawn_pro2", "Moondrop DAWN PRO2", device, errors)
    except ValueError as error:
        errors.append(f"Dawn Pro 2 HID: {error}")

    try:
        device = legacy_cls(config)
        display_name = getattr(device, "device_name", "Moondrop Dawn Pro")
        return BackendSelection("legacy", display_name, device, errors)
    except ValueError as error:
        errors.append(f"Original Dawn Pro USB: {error}")

    raise ValueError("No supported Moondrop device found.\n\n" + "\n".join(errors))
