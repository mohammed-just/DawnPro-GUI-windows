from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

from device.dawnpro2_hid import DawnPro2Hid, DawnPro2PeqBand

_NUMBER = r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)"
_FILTER_PATTERN = re.compile(
    rf"^\s*Filter\s+(?P<index>\d+)\s*:\s*"
    rf"(?P<state>ON|OFF)\s+"
    rf"(?P<filter>[A-Za-z0-9_]+)\s+"
    rf"Fc\s+(?P<frequency>{_NUMBER})\s*Hz"
    rf"(?:\s+Gain\s+(?P<gain>{_NUMBER})\s*dB)?"
    rf"\s+Q\s+(?P<q>{_NUMBER})\s*$",
    re.IGNORECASE,
)
_PREAMP_PATTERN = re.compile(
    rf"^\s*Preamp\s*:\s*(?P<gain>{_NUMBER})\s*dB\s*$",
    re.IGNORECASE,
)
_FILTER_ALIASES: Dict[str, str] = {
    "PK": "PEAKING",
    "PEQ": "PEAKING",
    "LS": "LOW_SHELF_2",
    "LSQ": "LOW_SHELF_2",
    "LSC": "LOW_SHELF_2",
    "HS": "HIGH_SHELF_2",
    "HSQ": "HIGH_SHELF_2",
    "HSC": "HIGH_SHELF_2",
    "LP": "LOW_PASS_2",
    "LPQ": "LOW_PASS_2",
    "HP": "HIGH_PASS_2",
    "HPQ": "HIGH_PASS_2",
}


class EqPresetError(ValueError):
    """Raised when an EQ preset cannot be safely imported."""


@dataclass(frozen=True)
class EqPreset:
    """Parsed Equalizer APO/AutoEQ-style preset."""

    bands: List[DawnPro2PeqBand]
    preamp: Optional[float] = None


def _validate_band(
    line_number: int,
    index: int,
    frequency: int,
    q_value: float,
    gain: float,
) -> None:
    if index < 1 or index > DawnPro2Hid.PEQ_COUNT:
        raise EqPresetError(
            f"Line {line_number}: filter number must be between 1 and "
            f"{DawnPro2Hid.PEQ_COUNT}."
        )
    if frequency < 20 or frequency > 20000:
        raise EqPresetError(
            f"Line {line_number}: frequency must be between 20 and 20000 Hz."
        )
    if q_value <= 0 or q_value > 127:
        raise EqPresetError(
            f"Line {line_number}: Q must be greater than 0 and at most 127."
        )
    if gain < -18 or gain > 12:
        raise EqPresetError(f"Line {line_number}: gain must be between -18 and 12 dB.")


def parse_eq_preset(text: str) -> EqPreset:
    """Parse the text preset format accepted by Moondrop Custom EQ."""
    bands_by_index: Dict[int, DawnPro2PeqBand] = {}
    preamp: Optional[float] = None

    for line_number, raw_line in enumerate(text.splitlines(), start=1):
        line = raw_line.strip()
        if not line or line.startswith(("#", ";", "//")):
            continue

        preamp_match = _PREAMP_PATTERN.fullmatch(line)
        if preamp_match:
            if preamp is not None:
                raise EqPresetError(f"Line {line_number}: duplicate Preamp line.")
            preamp = float(preamp_match.group("gain"))
            if preamp < -18 or preamp > 12:
                raise EqPresetError(
                    f"Line {line_number}: preamp must be between -18 and 12 dB."
                )
            continue

        filter_match = _FILTER_PATTERN.fullmatch(line)
        if not filter_match:
            raise EqPresetError(
                f"Line {line_number}: unsupported preset line.\n\n{raw_line}"
            )

        file_index = int(filter_match.group("index"))
        if file_index in bands_by_index:
            raise EqPresetError(f"Line {line_number}: duplicate Filter {file_index}.")

        filter_code = filter_match.group("filter").upper()
        filter_type = _FILTER_ALIASES.get(filter_code)
        if filter_type is None:
            supported = ", ".join(sorted(_FILTER_ALIASES))
            raise EqPresetError(
                f"Line {line_number}: unsupported filter type {filter_code}. "
                f"Supported types: {supported}."
            )

        frequency = int(round(float(filter_match.group("frequency"))))
        q_value = float(filter_match.group("q"))
        gain_text = filter_match.group("gain")
        gain = float(gain_text) if gain_text is not None else 0.0
        _validate_band(line_number, file_index, frequency, q_value, gain)

        bands_by_index[file_index] = DawnPro2PeqBand(
            index=file_index - 1,
            frequency=frequency,
            q=q_value,
            gain=gain,
            filter_type=filter_type,
            enabled=filter_match.group("state").upper() == "ON",
        )

    if not bands_by_index:
        raise EqPresetError("The file does not contain any supported Filter lines.")

    return EqPreset(
        bands=[bands_by_index[index] for index in sorted(bands_by_index)],
        preamp=preamp,
    )


def load_eq_preset(path: str | Path) -> EqPreset:
    """Load a UTF-8 or Windows-encoded text preset from disk."""
    preset_path = Path(path)
    try:
        text = preset_path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError:
        text = preset_path.read_text(encoding="cp1252")
    return parse_eq_preset(text)
