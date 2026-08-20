"""Capture real screenshots of the import dialog for the user guide.

Run with QGIS's own Python so the qgis.* modules resolve:

    "C:\\Program Files\\QGIS 3.44.12\\bin\\python-qgis-ltr.bat" docs\\make_screenshots.py

The dialog is driven exactly as a user drives it — detect, then import — but the
sidecar result is fed in from a saved JSON so the capture is instant and always
shows the same card. Grabbing the widget rather than photographing a screen
keeps the images sharp and free of whatever else is on the desktop.
"""

import json
import os
import sys
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent
SHOTS = HERE / "images"
SHOTS.mkdir(parents=True, exist_ok=True)

# The installed plugin folder is the one with bin/SmsImport.exe beside it.
PLUGIN = pathlib.Path(os.environ["APPDATA"]) / (
    "QGIS/QGIS3/profiles/default/python/plugins")
sys.path.insert(0, str(PLUGIN))

from qgis.core import QgsApplication  # noqa: E402
from qgis.PyQt.QtWidgets import QApplication  # noqa: E402

QgsApplication.setPrefixPath(os.environ.get("QGIS_PREFIX_PATH", ""), True)
app = QgsApplication([], True)
app.initQgis()

from smslibre_import.dialog import ImportDialog  # noqa: E402


class _StubIface:
    """Only mainWindow() is touched while the dialog is being built."""

    def mainWindow(self):
        return None


def shoot(widget, name):
    widget.repaint()
    QApplication.processEvents()
    path = SHOTS / f"{name}.png"
    widget.grab().save(str(path))
    print(f"  wrote {path.name}  ({path.stat().st_size // 1024} KB)")


def main():
    sample = json.loads((HERE / "sample_result.json").read_text())

    dlg = ImportDialog(_StubIface())
    # Wider than the 860 px default so all five result columns fit in the
    # figure, and compact rows so more of the table is visible at once.
    dlg.resize(1040, 640)
    dlg.table.verticalHeader().setDefaultSectionSize(26)
    dlg.show()
    QApplication.processEvents()

    # 1. as it opens
    dlg.card_edit.setText("")
    shoot(dlg, "dialog-1-empty")

    # 2. after Detect on a real card
    dlg.card_edit.setText(
        r"G:\Shared drives\Olds College Smart Farm Vault\1. Smart Farm"
        r"\Raw Equipment Data\2025\2025_Raw_Spraying_Data\Raven51025"
        r"\GFF\No Grower\No Farm\No Field\Jobs")
    dlg.detect_label.setText("<b>Detected:</b> Raven Viper 4 job (.jdp) (SMSLIBRE)")
    shoot(dlg, "dialog-2-detected")

    # 3. after a successful import — the real sidecar summary
    dlg._on_imported(sample)
    dlg.table.resizeColumnsToContents()
    dlg.table.horizontalHeader().setStretchLastSection(True)
    dlg.table.scrollToTop()
    QApplication.processEvents()
    shoot(dlg, "dialog-3-imported")

    # 4. Settings expanded, where the vendor paths live
    for child in dlg.findChildren(object):
        if type(child).__name__ == "QgsCollapsibleGroupBox":
            child.setCollapsed(False)
    QApplication.processEvents()
    shoot(dlg, "dialog-4-settings")

    # 5. the message shown when nothing recognises the folder — the single
    #    most common support question, so the guide shows it verbatim.
    blank = ImportDialog(_StubIface())
    blank.resize(1040, 380)
    blank.card_edit.setText(r"C:\Users\you\Documents\Some Folder")
    blank.detect_label.setText(
        "<b>No installed reader recognises this folder.</b> "
        "Point at the card's root (the folder written by the display), "
        "not a subfolder. Note SMS's own internal Vault is not readable — "
        "use the original card or export.")
    blank.show()
    QApplication.processEvents()
    shoot(blank, "dialog-5-not-recognised")
    blank.close()

    dlg.close()
    print("done")


if __name__ == "__main__":
    main()
