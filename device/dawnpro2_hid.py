import logging
import math
import time
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

try:
    import hid  # type: ignore
except ImportError:  # pragma: no cover - exercised only on systems without hidapi
    hid = None  # type: ignore

from device.config import AppConfig


@dataclass
class DawnPro2PeqBand:
    """Single Dawn Pro 2 PEQ band."""

    index: int
    frequency: int
    q: float
    gain: float
    filter_type: str
    enabled: bool = True


class DawnPro2Hid:
    """HID backend for Moondrop DAWN PRO2 FreeDSP controls."""

    VENDOR_ID = 0x35D8
    PRODUCT_ID = 0x011D
    REPORT_ID = 75
    REPORT_LENGTH = 64
    PAYLOAD_LENGTH = REPORT_LENGTH - 1
    SAMPLE_RATE = 96000
    PEQ_INDEX = 7
    PEQ_COUNT = 8

    WRITE = 0x01
    READ = 0x80
    FIRMWARE_VERSION = 12
    ACTIVE_EQ = 15
    UPDATE_EQ = 9
    UPDATE_EQ_COEFF_TO_REG = 10
    SAVE_EQ_TO_FLASH = 1
    DAC_OFFSET = 3
    PRE_GAIN = 35
    SAVE_OFFSET_TO_FLASH = 4
    CLEAR_FLASH = 5
    ERASE_CONFIG_ALL = 4
    ENTER_UPGRADE_MODE = 255

    FILTER_TYPES = {
        0: "DISABLED",
        1: "LOW_SHELF_2",
        2: "PEAKING",
        3: "HIGH_SHELF_2",
        4: "LOW_PASS_2",
        5: "HIGH_PASS_2",
    }
    FILTER_LABELS = {
        "DISABLED": "Disabled",
        "LOW_SHELF_2": "Low Shelf 2",
        "PEAKING": "Peaking",
        "HIGH_SHELF_2": "High Shelf 2",
        "LOW_PASS_2": "Low Pass 2",
        "HIGH_PASS_2": "High Pass 2",
    }
    FILTER_CODES = {name: code for code, name in FILTER_TYPES.items()}
    LABEL_TO_FILTER = {label: name for name, label in FILTER_LABELS.items()}

    def __init__(self, config: AppConfig) -> None:
        self.config = config
        self.device_name = "Moondrop DAWN PRO2"
        self._device_info = self._find_device_info()
        if self._device_info is None:
            raise ValueError(
                "Dawn Pro 2 HID interface not found. Ensure DAWN PRO2 is connected "
                "and visible as HID VID=0x35D8 PID=0x011D."
            )

    @classmethod
    def _require_hid(cls) -> None:
        if hid is None:
            raise ValueError(
                "hidapi is not installed. Install hidapi on Windows, or python3-hid/"
                "hidapi runtime packages on Linux."
            )

    @classmethod
    def enumerate_devices(cls) -> List[Dict[str, Any]]:
        cls._require_hid()
        return list(hid.enumerate())  # type: ignore[union-attr]

    @classmethod
    def is_available(cls) -> bool:
        cls._require_hid()
        return bool(hid.enumerate(cls.VENDOR_ID, cls.PRODUCT_ID))  # type: ignore[union-attr]

    @classmethod
    def _find_device_info(cls) -> Optional[Dict[str, Any]]:
        cls._require_hid()
        devices = hid.enumerate(cls.VENDOR_ID, cls.PRODUCT_ID)  # type: ignore[union-attr]
        return devices[0] if devices else None

    def _open(self) -> Any:
        device_info = self._find_device_info()
        if device_info is None:
            raise IOError("Dawn Pro 2 HID interface is no longer available.")

        device = hid.device()  # type: ignore[union-attr]
        device.open_path(device_info["path"])
        device.set_nonblocking(False)
        return device

    def _create_packet(self, payload: List[int]) -> List[int]:
        packet = [self.REPORT_ID] + payload
        if len(packet) < self.REPORT_LENGTH:
            packet.extend([0] * (self.REPORT_LENGTH - len(packet)))
        return packet[: self.REPORT_LENGTH]

    def _normalize_response(self, response: List[int]) -> List[int]:
        if not response:
            raise IOError("Timed out waiting for Dawn Pro 2 response.")
        if response[0] == self.REPORT_ID:
            return response[1:]
        if len(response) == self.PAYLOAD_LENGTH:
            return response
        logging.debug("Unexpected HID response shape: %s", response)
        return response[1:]

    def _send(
        self,
        payload: List[int],
        expect_response: bool = True,
        timeout_ms: int = 2000,
    ) -> List[int]:
        device = self._open()
        try:
            device.write(self._create_packet(payload))
            if not expect_response:
                return []
            return self._normalize_response(device.read(self.REPORT_LENGTH, timeout_ms=timeout_ms))
        finally:
            device.close()

    @staticmethod
    def _decode_fixed_point(low_byte: int, high_byte: int) -> float:
        raw_value = int.from_bytes(bytes([low_byte, high_byte]), byteorder="little", signed=True)
        return raw_value / 256.0

    @staticmethod
    def _encode_fixed_point(value: float) -> List[int]:
        raw_value = int(round(value * 256))
        return [raw_value & 0xFF, (raw_value >> 8) & 0xFF]

    @classmethod
    def normalize_filter_type(cls, value: str) -> str:
        if value in cls.FILTER_CODES:
            return value
        return cls.LABEL_TO_FILTER.get(value, "PEAKING")

    @classmethod
    def filter_label(cls, value: str) -> str:
        return cls.FILTER_LABELS.get(cls.normalize_filter_type(value), value)

    @staticmethod
    def _scale_coefficients(numerator: List[float], denominator: List[float]) -> List[int]:
        scaled_num = [round(value * 1073741824) for value in numerator]
        scaled_den = [round(value * 1073741824) for value in denominator]
        return [scaled_den[0], scaled_den[1], scaled_den[2], -scaled_num[1], -scaled_num[2]]

    @classmethod
    def _coefficients_to_bytes(cls, coefficients: List[int]) -> List[int]:
        result: List[int] = []
        for coefficient in coefficients:
            result.extend(
                int(coefficient).to_bytes(4, byteorder="little", signed=True)
            )
        return result

    @classmethod
    def _low_shelf_2(cls, frequency: int, gain: float, q: float) -> List[int]:
        amp = 10 ** (gain / 40)
        omega = frequency * math.pi * 2 / cls.SAMPLE_RATE
        alpha = math.sin(omega) / 2 * math.sqrt((amp + 1 / amp) * (1 / q - 1) + 2)
        cos_omega = math.cos(omega)
        divisor = amp + 1 + (amp - 1) * cos_omega + 2 * math.sqrt(amp) * alpha
        numerator = [
            1,
            -2 * (amp - 1 + (amp + 1) * cos_omega) / divisor,
            (amp + 1 + (amp - 1) * cos_omega - 2 * math.sqrt(amp) * alpha) / divisor,
        ]
        denominator = [
            amp * (amp + 1 - (amp - 1) * cos_omega + 2 * math.sqrt(amp) * alpha) / divisor,
            2 * amp * (amp - 1 - (amp + 1) * cos_omega) / divisor,
            amp * (amp + 1 - (amp - 1) * cos_omega - 2 * math.sqrt(amp) * alpha) / divisor,
        ]
        return cls._scale_coefficients(numerator, denominator)

    @classmethod
    def _peaking(cls, frequency: int, gain: float, q: float) -> List[int]:
        amp = math.sqrt(10 ** (gain / 20))
        omega = frequency * math.pi * 2 / cls.SAMPLE_RATE
        alpha = math.sin(omega) / (2 * q)
        cos_omega = math.cos(omega)
        divisor = alpha / amp + 1
        numerator = [1, cos_omega * -2 / divisor, (1 - alpha / amp) / divisor]
        denominator = [
            (alpha * amp + 1) / divisor,
            cos_omega * -2 / divisor,
            (1 - alpha * amp) / divisor,
        ]
        return cls._scale_coefficients(numerator, denominator)

    @classmethod
    def _high_shelf_2(cls, frequency: int, gain: float, q: float) -> List[int]:
        amp = 10 ** (gain / 40)
        omega = frequency * math.pi * 2 / cls.SAMPLE_RATE
        alpha = math.sin(omega) / 2 * math.sqrt((amp + 1 / amp) * (1 / q - 1) + 2)
        cos_omega = math.cos(omega)
        divisor = amp + 1 - (amp - 1) * cos_omega + 2 * math.sqrt(amp) * alpha
        numerator = [
            1,
            2 * (amp - 1 - (amp + 1) * cos_omega) / divisor,
            (amp + 1 - (amp - 1) * cos_omega - 2 * math.sqrt(amp) * alpha) / divisor,
        ]
        denominator = [
            amp * (amp + 1 + (amp - 1) * cos_omega + 2 * math.sqrt(amp) * alpha) / divisor,
            -2 * amp * (amp - 1 + (amp + 1) * cos_omega) / divisor,
            amp * (amp + 1 + (amp - 1) * cos_omega - 2 * math.sqrt(amp) * alpha) / divisor,
        ]
        return cls._scale_coefficients(numerator, denominator)

    @classmethod
    def _low_pass_2(cls, frequency: int, q: float) -> List[int]:
        omega = frequency * math.pi * 2 / cls.SAMPLE_RATE
        alpha = math.sin(omega) / (2 * q)
        cos_omega = math.cos(omega)
        divisor = alpha + 1
        numerator = [1, cos_omega * -2 / divisor, (1 - alpha) / divisor]
        denominator = [
            (1 - cos_omega) / 2 / divisor,
            (1 - cos_omega) / divisor,
            (1 - cos_omega) / 2 / divisor,
        ]
        return cls._scale_coefficients(numerator, denominator)

    @classmethod
    def _high_pass_2(cls, frequency: int, q: float) -> List[int]:
        omega = frequency * math.pi * 2 / cls.SAMPLE_RATE
        alpha = math.sin(omega) / (2 * q)
        cos_omega = math.cos(omega)
        divisor = alpha + 1
        numerator = [1, cos_omega * -2 / divisor, (1 - alpha) / divisor]
        denominator = [
            (1 + cos_omega) / 2 / divisor,
            (-1 - cos_omega) / divisor,
            (1 + cos_omega) / 2 / divisor,
        ]
        return cls._scale_coefficients(numerator, denominator)

    @classmethod
    def generate_peq_coefficients(
        cls,
        frequency: int,
        gain: float,
        q: float,
        filter_type: str,
    ) -> List[int]:
        normalized_filter = cls.normalize_filter_type(filter_type)
        if normalized_filter == "DISABLED":
            return [0] * 20
        if frequency < 20 or frequency > 20000:
            raise ValueError("frequency must be between 20 and 20000 Hz")
        if q <= 0:
            raise ValueError("q must be greater than 0")

        if normalized_filter == "LOW_SHELF_2":
            coefficients = cls._low_shelf_2(frequency, gain, q)
        elif normalized_filter == "PEAKING":
            coefficients = cls._peaking(frequency, gain, q)
        elif normalized_filter == "HIGH_SHELF_2":
            coefficients = cls._high_shelf_2(frequency, gain, q)
        elif normalized_filter == "LOW_PASS_2":
            coefficients = cls._low_pass_2(frequency, q)
        elif normalized_filter == "HIGH_PASS_2":
            coefficients = cls._high_pass_2(frequency, q)
        else:
            raise ValueError(f"Invalid filter type: {filter_type}")

        return cls._coefficients_to_bytes(coefficients)

    def read_firmware_version(self) -> str:
        payload = self._send([self.READ, self.FIRMWARE_VERSION, 0])
        raw = bytes(payload[3:])
        return raw.split(b"\x00", 1)[0].decode("utf-8", errors="ignore")

    def read_eq_index(self) -> int:
        payload = self._send([self.READ, self.ACTIVE_EQ, 0])
        return payload[3]

    def write_eq_index(self, index: int, save: bool = False) -> None:
        if index < 0 or index > 15:
            raise ValueError("EQ index must be between 0 and 15")
        self._send([self.WRITE, self.ACTIVE_EQ, 0, index], expect_response=False)
        time.sleep(0.05)
        if save:
            self.save_eq_to_flash()

    def read_pre_gain(self) -> float:
        payload = self._send([self.READ, self.PRE_GAIN, 0])
        return self._decode_fixed_point(payload[3], payload[4])

    def write_pre_gain(self, value: float, save: bool = False) -> None:
        self._validate_gain(value)
        self._send([self.WRITE, self.PRE_GAIN, 0, *self._encode_fixed_point(value)], expect_response=False)
        time.sleep(0.05)
        if save:
            self.save_offset_to_flash()

    def read_global_gain(self) -> float:
        payload = self._send([self.READ, self.DAC_OFFSET, 0])
        return self._decode_fixed_point(payload[3], payload[4])

    def write_global_gain(self, value: float, save: bool = False) -> None:
        self._validate_gain(value)
        self._send([self.WRITE, self.DAC_OFFSET, 0, *self._encode_fixed_point(value)], expect_response=False)
        time.sleep(0.05)
        if save:
            self.save_offset_to_flash()

    @staticmethod
    def _validate_gain(value: float) -> None:
        if value < -18 or value > 12:
            raise ValueError("gain must be between -18 and 12 dB")

    def read_peq_band(self, index: int) -> DawnPro2PeqBand:
        self._validate_peq_index(index)
        payload = self._send([self.READ, self.UPDATE_EQ, 0, 0, index])
        frequency = int.from_bytes(bytes(payload[27:29]), byteorder="little", signed=False)
        q_value = self._decode_fixed_point(payload[29], payload[30])
        gain = self._decode_fixed_point(payload[31], payload[32])
        filter_code = payload[33]
        filter_type = self.FILTER_TYPES.get(filter_code, f"UNKNOWN_{filter_code}")
        return DawnPro2PeqBand(
            index=index,
            frequency=frequency,
            q=q_value,
            gain=gain,
            filter_type=filter_type,
            enabled=filter_type != "DISABLED",
        )

    def read_all_peq_bands(self) -> List[DawnPro2PeqBand]:
        return [self.read_peq_band(index) for index in range(self.PEQ_COUNT)]

    def write_peq_band(self, index: int, band: DawnPro2PeqBand) -> None:
        self._validate_peq_index(index)
        filter_type = self.normalize_filter_type(band.filter_type)
        if not band.enabled:
            filter_type = "DISABLED"

        payload = [0] * self.PAYLOAD_LENGTH
        payload[0] = self.WRITE
        payload[1] = self.UPDATE_EQ
        payload[2] = 0
        payload[3] = 0
        payload[4] = index
        payload[5] = 0
        payload[6] = 0
        payload[7:27] = self.generate_peq_coefficients(
            band.frequency, band.gain, band.q, filter_type
        )
        payload[27] = band.frequency & 0xFF
        payload[28] = (band.frequency >> 8) & 0xFF
        payload[29:31] = self._encode_fixed_point(band.q)
        payload[31:33] = self._encode_fixed_point(band.gain)
        payload[33] = self.FILTER_CODES.get(filter_type, self.FILTER_CODES["PEAKING"])
        payload[34] = 0
        payload[35] = self.PEQ_INDEX
        self._send(payload, expect_response=False)

    def enable_peq_band(self, index: int) -> None:
        self._validate_peq_index(index)
        payload = [0] * self.PAYLOAD_LENGTH
        payload[0] = self.WRITE
        payload[1] = self.UPDATE_EQ_COEFF_TO_REG
        payload[2] = index
        payload[3] = 0
        payload[4] = 0xFF
        payload[5] = 0xFF
        payload[6] = 0xFF
        self._send(payload, expect_response=False)

    def write_all_peq_bands(self, bands: List[DawnPro2PeqBand], save: bool = False) -> None:
        for index, band in enumerate(bands[: self.PEQ_COUNT]):
            self.write_peq_band(index, band)
            time.sleep(0.025)
            self.enable_peq_band(index)
            time.sleep(0.05)
        if save:
            self.save_eq_to_flash()

    def save_eq_to_flash(self) -> None:
        self._send([self.WRITE, self.SAVE_EQ_TO_FLASH, 0], expect_response=False)
        time.sleep(0.2)

    def save_offset_to_flash(self) -> None:
        self._send([self.WRITE, self.SAVE_OFFSET_TO_FLASH, 0], expect_response=False)
        time.sleep(0.2)

    def clear_flash(self) -> None:
        self._send([self.WRITE, self.CLEAR_FLASH, 0, self.ERASE_CONFIG_ALL], expect_response=False)

    @classmethod
    def _validate_peq_index(cls, index: int) -> None:
        if index < 0 or index >= cls.PEQ_COUNT:
            raise ValueError(f"PEQ band index must be between 0 and {cls.PEQ_COUNT - 1}")
