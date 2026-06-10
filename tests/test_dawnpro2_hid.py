from typing import List

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
