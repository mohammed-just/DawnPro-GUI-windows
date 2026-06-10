import json

from device.config import AppConfig


def test_load_old_config_without_dawn_pro2_section(tmp_path) -> None:
    config_path = tmp_path / "config.json"
    config_path.write_text(
        json.dumps(
            {
                "default_settings": {
                    "DEFAULT_VOLUME": 42,
                    "DEFAULT_LED_STATUS": "Off",
                    "DEFAULT_GAIN": "High",
                    "DEFAULT_FILTER": "Non-Oversampling",
                }
            }
        ),
        encoding="utf-8",
    )

    config = AppConfig.load_from_file(str(config_path))

    assert config.default_settings.DEFAULT_VOLUME == 42
    assert config.dawn_pro2_settings.DEFAULT_EQ_INDEX == 0
    assert config.dawn_pro2_settings.DEFAULT_PRE_GAIN == 0.0
    assert config.dawn_pro2_settings.DEFAULT_GLOBAL_GAIN == 0.0


def test_load_config_ignores_unknown_keys(tmp_path) -> None:
    config_path = tmp_path / "config.json"
    config_path.write_text(
        json.dumps(
            {
                "device_identifiers": {
                    "MOONDROP_VID": 0x2FC6,
                    "DAWN_PRO_PID": 0xF06A,
                    "UNKNOWN_NEW_KEY": "ignored",
                },
                "dawn_pro2_settings": {
                    "DEFAULT_EQ_INDEX": 7,
                    "DEFAULT_PRE_GAIN": -3.0,
                    "DEFAULT_GLOBAL_GAIN": 1.5,
                    "FUTURE": True,
                },
            }
        ),
        encoding="utf-8",
    )

    config = AppConfig.load_from_file(str(config_path))

    assert config.device_identifiers.MOONDROP_VID == 0x2FC6
    assert config.dawn_pro2_settings.DEFAULT_EQ_INDEX == 7
    assert config.dawn_pro2_settings.DEFAULT_PRE_GAIN == -3.0
    assert config.dawn_pro2_settings.DEFAULT_GLOBAL_GAIN == 1.5
