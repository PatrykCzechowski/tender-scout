import tender_scout


def test_package_imports() -> None:
    assert hasattr(tender_scout, "main")
