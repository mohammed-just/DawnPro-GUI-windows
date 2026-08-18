import pytest

from device.dawnpro2_hid import DawnPro2PeqBand
from device.eq_presets import EqPresetError, parse_eq_preset

SENHIZER_PRESET = """\
Filter 1: ON LSQ Fc 25 Hz Gain 6.0 dB Q 0.710
Filter 2: ON LSQ Fc 105 Hz Gain 4.5 dB Q 0.710
Filter 3: ON PK Fc 160 Hz Gain -3.0 dB Q 0.550
Filter 4: ON PK Fc 1350 Hz Gain -2.2 dB Q 1.500
Filter 5: ON HSQ Fc 1900 Hz Gain 4.5 dB Q 0.710
Filter 6: ON PK Fc 3250 Hz Gain -3.8 dB Q 2.100
Filter 7: ON PK Fc 5400 Hz Gain -7.0 dB Q 3.500
Filter 8: ON HSQ Fc 11000 Hz Gain -4.0 dB Q 0.710
"""


def test_parses_senhizer_online_app_preset() -> None:
    preset = parse_eq_preset(SENHIZER_PRESET)

    assert preset.preamp is None
    assert len(preset.bands) == 8
    assert preset.bands[0] == DawnPro2PeqBand(
        index=0,
        frequency=25,
        q=0.710,
        gain=6.0,
        filter_type="LOW_SHELF_2",
        enabled=True,
    )
    assert preset.bands[4].filter_type == "HIGH_SHELF_2"
    assert preset.bands[7].frequency == 11000


def test_parses_preamp_off_band_and_pass_filters_without_gain() -> None:
    preset = parse_eq_preset("""\
Preamp: -5.5 dB
Filter 1: OFF PK Fc 1000 Hz Gain -2 dB Q 1.4
Filter 5: ON LPQ Fc 15000 Hz Q 0.707
Filter 8: ON HP Fc 25 Hz Q 0.71
""")

    assert preset.preamp == -5.5
    assert [band.index for band in preset.bands] == [0, 4, 7]
    assert preset.bands[0].enabled is False
    assert preset.bands[1].filter_type == "LOW_PASS_2"
    assert preset.bands[1].gain == 0.0
    assert preset.bands[2].filter_type == "HIGH_PASS_2"


@pytest.mark.parametrize(
    ("line", "message"),
    [
        ("Filter 9: ON PK Fc 1000 Hz Gain 0 dB Q 1", "filter number"),
        ("Filter 1: ON PK Fc 10 Hz Gain 0 dB Q 1", "frequency"),
        ("Filter 1: ON PK Fc 1000 Hz Gain 13 dB Q 1", "gain"),
        ("Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 0", "Q"),
        ("Filter 1: ON NOTCH Fc 1000 Hz Gain 0 dB Q 1", "unsupported filter"),
    ],
)
def test_rejects_values_the_device_cannot_apply(line: str, message: str) -> None:
    with pytest.raises(EqPresetError, match=message):
        parse_eq_preset(line)


def test_rejects_duplicate_filter_numbers_before_device_write() -> None:
    with pytest.raises(EqPresetError, match="duplicate Filter 1"):
        parse_eq_preset("""\
Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 1
Filter 1: ON PK Fc 2000 Hz Gain 0 dB Q 1
""")


def test_rejects_unknown_non_comment_lines() -> None:
    with pytest.raises(EqPresetError, match="unsupported preset line"):
        parse_eq_preset("""\
# generated preset
Filter 1: ON PK Fc 1000 Hz Gain 0 dB Q 1
GraphicEQ: 20 0; 40 1
""")
