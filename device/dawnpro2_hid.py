import time
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

import hid

from device.config import AppConfig


@dataclass
class DawnPro2PeqBand:
    """Single Dawn Pro 2 PEQ band."""
    index: int
    frequency: int
    q: float
    gain: float
    filter_type: str
    enabled: bool


class DawnPro2Hid:
    """HID backend for the Moondrop Dawn Pro 2."""

    VENDOR_ID = 0x35D8
    PRODUCT_ID = 0x011D
    REPORT_ID = 75
    REPORT_LENGTH = 64

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

    FILTER_TYPES = {
        0: 'Disabled',
        1: 'Low Shelf 2',
        2: 'Peaking',
        3: 'High Shelf 2',
        4: 'Low Pass 2',
        5: 'High Pass 2',
    }

    def __init__(self, config: AppConfig) -> None:
        self.config = config
        self.device_name = 'Moondrop Dawn Pro 2'
        self._device_info = self._find_device_info()
        if self._device_info is None:
            raise ValueError(
                'Dawn Pro 2 HID interface not found. Ensure the device is connected and visible as HID.'
            )

    @classmethod
    def is_available(cls) -> bool:
        return bool(hid.enumerate(cls.VENDOR_ID, cls.PRODUCT_ID))

    @classmethod
    def _find_device_info(cls) -> Optional[Dict[str, Any]]:
        devices = hid.enumerate(cls.VENDOR_ID, cls.PRODUCT_ID)
        return devices[0] if devices else None

    def _open(self) -> hid.device:
        device_info = self._find_device_info()
        if device_info is None:
            raise IOError('Dawn Pro 2 HID interface is no longer available.')

        device = hid.device()
        device.open_path(device_info['path'])
        device.set_nonblocking(False)
        return device

    def _create_packet(self, payload: List[int]) -> List[int]:
        packet = [self.REPORT_ID] + payload
        if len(packet) < self.REPORT_LENGTH:
            packet.extend([0] * (self.REPORT_LENGTH - len(packet)))
        return packet[:self.REPORT_LENGTH]

    def _send(self, payload: List[int], expect_response: bool = True, timeout_ms: int = 2000) -> List[int]:
        device = self._open()
        try:
            packet = self._create_packet(payload)
            device.write(packet)
            if not expect_response:
                return []

            response = device.read(self.REPORT_LENGTH, timeout_ms=timeout_ms)
            if not response:
                raise IOError('Timed out waiting for Dawn Pro 2 response.')
            if response[0] != self.REPORT_ID:
                raise IOError(f'Unexpected report ID: {response[0]}')
            return response[1:]
        finally:
            device.close()

    @staticmethod
    def _decode_fixed_point(low_byte: int, high_byte: int) -> float:
        raw_value = int.from_bytes(bytes([low_byte, high_byte]), byteorder='little', signed=True)
        return raw_value / 256.0

    def read_firmware_version(self) -> str:
        payload = self._send([self.READ, self.FIRMWARE_VERSION, 0])
        raw = bytes(payload[3:])
        return raw.split(b'\x00', 1)[0].decode('utf-8', errors='ignore')

    def read_eq_index(self) -> int:
        payload = self._send([self.READ, self.ACTIVE_EQ, 0])
        return payload[3]

    def write_eq_index(self, index: int, save: bool = False) -> None:
        self._send([self.WRITE, self.ACTIVE_EQ, 0, index], expect_response=False)
        time.sleep(0.05)
        if save:
            self.save_eq_to_flash()

    def read_pre_gain(self) -> float:
        payload = self._send([self.READ, self.PRE_GAIN, 0])
        return self._decode_fixed_point(payload[3], payload[4])

    def write_pre_gain(self, value: float, save: bool = False) -> None:
        raw_value = int(round(value * 256))
        payload = [self.WRITE, self.PRE_GAIN, 0, raw_value & 0xFF, (raw_value >> 8) & 0xFF]
        self._send(payload, expect_response=False)
        time.sleep(0.05)
        if save:
            self.save_offset_to_flash()

    def read_global_gain(self) -> float:
        payload = self._send([self.READ, self.DAC_OFFSET, 0])
        return self._decode_fixed_point(payload[3], payload[4])

    def write_global_gain(self, value: float, save: bool = False) -> None:
        raw_value = int(round(value * 256))
        payload = [self.WRITE, self.DAC_OFFSET, 0, raw_value & 0xFF, (raw_value >> 8) & 0xFF]
        self._send(payload, expect_response=False)
        time.sleep(0.05)
        if save:
            self.save_offset_to_flash()

    def read_peq_band(self, index: int) -> DawnPro2PeqBand:
        payload = self._send([self.READ, self.UPDATE_EQ, 0, 0, index])
        frequency = int.from_bytes(bytes(payload[27:29]), byteorder='little', signed=False)
        q_value = self._decode_fixed_point(payload[29], payload[30])
        gain = self._decode_fixed_point(payload[31], payload[32])
        filter_code = payload[33]
        enabled = payload[35] != 0
        return DawnPro2PeqBand(
            index=index,
            frequency=frequency,
            q=q_value,
            gain=gain,
            filter_type=self.FILTER_TYPES.get(filter_code, f'Unknown ({filter_code})'),
            enabled=enabled,
        )

    def read_all_peq_bands(self) -> List[DawnPro2PeqBand]:
        return [self.read_peq_band(index) for index in range(8)]

    def save_eq_to_flash(self) -> None:
        self._send([self.WRITE, self.SAVE_EQ_TO_FLASH, 0], expect_response=False)
        time.sleep(0.2)

    def save_offset_to_flash(self) -> None:
        self._send([self.WRITE, self.SAVE_OFFSET_TO_FLASH, 0], expect_response=False)
        time.sleep(0.2)