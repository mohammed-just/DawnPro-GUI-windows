from typing import List

import pytest

from device.dawnpro2_hid import DawnPro2Hid, DawnPro2PeqBand


def make_backend_without_hardware() -> DawnPro2Hid:
    return DawnPro2Hid.__new__(DawnPro2Hid)


def test_packet_layout_uses_report_id_and_64_bytes() -> None:
    backend = make_backend_without_hardware()

    packet = backend._create_packet([DawnPro2Hid.READ, DawnPro2Hid.FIRMWARE_VERSION, 0])

    assert len(packet) == 64
    assert packet[:4] == [75, 128, 12, 0]
    assert packet[-1] == 0


def test_fixed_point_roundtrip_for_signed_gain() -> None:
    assert DawnPro2Hid._decode_fixed_point(*DawnPro2Hid._encode_fixed_point(-2.5)) == -2.5
    assert DawnPro2Hid._decode_fixed_point(*DawnPro2Hid._encode_fixed_point(1.25)) == 1.25


def test_disabled_coefficients_are_zeroed() -> None:
    coefficients = DawnPro2Hid.generate_peq_coefficients(1000, 0.0, 1.0, "DISABLED")

    assert coefficients == [0] * 20


def test_peaking_coefficients_are_packed_as_20_bytes() -> None:
    coefficients = DawnPro2Hid.generate_peq_coefficients(1000, 3.0, 1.0, "PEAKING")

    assert len(coefficients) == 20
    assert coefficients != [0] * 20
    assert all(0 <= byte <= 255 for byte in coefficients)


def test_high_shelf_coefficients_match_web_app_32_bit_wrapping() -> None:
    coefficients = DawnPro2Hid.generate_peq_coefficients(
        1900, 4.5, 0.71, "HIGH_SHELF_2"
    )

    assert coefficients == [
        0x1F,
        0xB7,
        0xA4,
        0x68,
        0x5D,
        0xBD,
        0x7B,
        0x41,
        0xDE,
        0x38,
        0x04,
        0x57,
        0x70,
        0x9E,
        0x40,
        0x71,
        0x36,
        0xB4,
        0x9A,
        0xCD,
    ]


def test_read_peq_band_parses_moondrop_offsets() -> None:
    backend = make_backend_without_hardware()
    response = [0] * DawnPro2Hid.PAYLOAD_LENGTH
    response[27] = 0xE8
    response[28] = 0x03
    response[29:31] = DawnPro2Hid._encode_fixed_point(1.25)
    response[31:33] = DawnPro2Hid._encode_fixed_point(-2.5)
    response[33] = DawnPro2Hid.FILTER_CODES["PEAKING"]
    backend._send = lambda *args, **kwargs: response  # type: ignore[method-assign]

    band = backend.read_peq_band(3)

    assert band == DawnPro2PeqBand(
        index=3,
        frequency=1000,
        q=1.25,
        gain=-2.5,
        filter_type="PEAKING",
        enabled=True,
    )


def test_write_peq_band_uses_moondrop_payload_layout() -> None:
    backend = make_backend_without_hardware()
    sent_payloads: List[List[int]] = []

    def fake_send(payload, expect_response=True, timeout_ms=2000):
        sent_payloads.append(payload)
        return []

    backend._send = fake_send  # type: ignore[method-assign]
    band = DawnPro2PeqBand(
        index=2,
        frequency=1000,
        q=1.0,
        gain=3.0,
        filter_type="Peaking",
        enabled=True,
    )

    backend.write_peq_band(2, band)
    backend.enable_peq_band(2)

    write_payload = sent_payloads[0]
    assert len(write_payload) == 63
    assert write_payload[0] == DawnPro2Hid.WRITE
    assert write_payload[1] == DawnPro2Hid.UPDATE_EQ
    assert write_payload[4] == 2
    assert write_payload[7:27] != [0] * 20
    assert write_payload[27] == 0xE8
    assert write_payload[28] == 0x03
    assert write_payload[33] == DawnPro2Hid.FILTER_CODES["PEAKING"]
    assert write_payload[35] == DawnPro2Hid.PEQ_INDEX

    enable_payload = sent_payloads[1]
    assert enable_payload[:7] == [
        DawnPro2Hid.WRITE,
        DawnPro2Hid.UPDATE_EQ_COEFF_TO_REG,
        2,
        0,
        0xFF,
        0xFF,
        0xFF,
    ]


def test_write_all_peq_bands_respects_imported_band_indexes(monkeypatch) -> None:
    backend = make_backend_without_hardware()
    written_indexes: List[int] = []
    enabled_indexes: List[int] = []
    monkeypatch.setattr("device.dawnpro2_hid.time.sleep", lambda _seconds: None)
    backend.write_peq_band = (  # type: ignore[method-assign]
        lambda index, _band: written_indexes.append(index)
    )
    backend.enable_peq_band = enabled_indexes.append  # type: ignore[method-assign]
    bands = [
        DawnPro2PeqBand(2, 1000, 1.0, 0.0, "PEAKING"),
        DawnPro2PeqBand(7, 12000, 0.7, -2.0, "HIGH_SHELF_2"),
    ]

    backend.write_all_peq_bands(bands)

    assert written_indexes == [2, 7]
    assert enabled_indexes == [2, 7]


def test_write_all_peq_bands_validates_every_index_before_writing() -> None:
    backend = make_backend_without_hardware()
    written_indexes: List[int] = []
    backend.write_peq_band = (  # type: ignore[method-assign]
        lambda index, _band: written_indexes.append(index)
    )
    bands = [
        DawnPro2PeqBand(2, 1000, 1.0, 0.0, "PEAKING"),
        DawnPro2PeqBand(2, 12000, 0.7, -2.0, "HIGH_SHELF_2"),
    ]

    with pytest.raises(ValueError, match="duplicate PEQ band index"):
        backend.write_all_peq_bands(bands)

    assert written_indexes == []
