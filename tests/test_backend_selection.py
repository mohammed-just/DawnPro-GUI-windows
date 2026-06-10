import pytest

from device.backends import select_backend
from device.config import AppConfig


class AvailableDawnPro2:
    def __init__(self, config: AppConfig) -> None:
        self.config = config


class MissingDawnPro2:
    def __init__(self, config: AppConfig) -> None:
        raise ValueError("Dawn Pro 2 missing")


class AvailableLegacy:
    device_name = "Legacy Dawn Pro"

    def __init__(self, config: AppConfig) -> None:
        self.config = config


class MissingLegacy:
    def __init__(self, config: AppConfig) -> None:
        raise ValueError("Legacy missing")


def test_select_backend_prefers_dawn_pro2() -> None:
    selection = select_backend(
        AppConfig(),
        dawn_pro2_cls=AvailableDawnPro2,
        legacy_cls=AvailableLegacy,
    )

    assert selection.kind == "dawn_pro2"
    assert selection.display_name == "Moondrop DAWN PRO2"


def test_select_backend_falls_back_to_legacy() -> None:
    selection = select_backend(
        AppConfig(),
        dawn_pro2_cls=MissingDawnPro2,
        legacy_cls=AvailableLegacy,
    )

    assert selection.kind == "legacy"
    assert selection.display_name == "Legacy Dawn Pro"
    assert selection.errors == ["Dawn Pro 2 HID: Dawn Pro 2 missing"]


def test_select_backend_reports_both_failures() -> None:
    with pytest.raises(ValueError) as error:
        select_backend(
            AppConfig(),
            dawn_pro2_cls=MissingDawnPro2,
            legacy_cls=MissingLegacy,
        )

    message = str(error.value)
    assert "Dawn Pro 2 HID: Dawn Pro 2 missing" in message
    assert "Original Dawn Pro USB: Legacy missing" in message
