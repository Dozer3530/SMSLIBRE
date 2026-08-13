"""Import dialog: pick a card folder, detect the format, convert, add layers."""

from __future__ import annotations

import os
import tempfile
from datetime import datetime

from qgis.core import QgsProject, QgsSettings, QgsVectorLayer
from qgis.PyQt.QtCore import Qt, QThread, pyqtSignal
from qgis.PyQt.QtWidgets import (
    QAbstractItemView, QCheckBox, QDialog, QDialogButtonBox, QFileDialog,
    QGridLayout, QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit,
    QMessageBox, QProgressBar, QPushButton, QTableWidget, QTableWidgetItem,
    QVBoxLayout, QWidget,
)

from .sidecar import Sidecar, SidecarError, default_sidecar_path, default_sms_dir
from .styling import apply_yield_style

SETTINGS_PREFIX = "smslibre_import"


def _output_dir() -> str:
    """Durable folder for imported GeoPackages.

    Map layers reference these files, so they must outlive a temp sweep.
    Honours the `output_dir` setting when set, else ~/Documents/SMSLIBRE.
    """
    configured = QgsSettings().value(f"{SETTINGS_PREFIX}/output_dir", "")
    base = configured or os.path.join(
        os.path.expanduser("~"), "Documents", "SMSLIBRE")
    try:
        os.makedirs(base, exist_ok=True)
        return base
    except OSError:
        return tempfile.gettempdir()      # last resort

# QgsCollapsibleGroupBox gives a native, state-remembering collapse header.
# Fall back to a plain QGroupBox if the QGIS gui module is unavailable.
try:
    from qgis.gui import QgsCollapsibleGroupBox

    def _make_group(title: str, collapsed: bool = True):
        box = QgsCollapsibleGroupBox(title)
        box.setSaveCollapsedState(True)
        box.setCollapsed(collapsed)
        return box

except ImportError:                                   # pragma: no cover
    def _make_group(title: str, collapsed: bool = True):
        return QGroupBox(title)


class _ImportWorker(QThread):
    """Runs the sidecar off the UI thread; a big card can take minutes."""

    finished_ok = pyqtSignal(dict)
    failed = pyqtSignal(str)

    def __init__(self, sidecar: Sidecar, card: str, out_gpkg: str, plugin: str | None):
        super().__init__()
        self._sidecar = sidecar
        self._card = card
        self._out = out_gpkg
        self._plugin = plugin

    def run(self):
        try:
            self.finished_ok.emit(
                self._sidecar.import_card(self._card, self._out, self._plugin))
        except SidecarError as exc:
            self.failed.emit(str(exc))
        except Exception as exc:                      # pragma: no cover
            self.failed.emit(f"Unexpected error: {exc}")


class ImportDialog(QDialog):
    def __init__(self, iface, parent=None):
        super().__init__(parent)
        self.iface = iface
        self.setWindowTitle("Import Machine Data")
        self.resize(860, 620)
        self._result = None
        self._worker = None
        self._detected = []

        s = QgsSettings()
        sms = s.value(f"{SETTINGS_PREFIX}/sms_dir", "") or default_sms_dir()
        exe = s.value(f"{SETTINGS_PREFIX}/sidecar", "") or default_sidecar_path()

        layout = QVBoxLayout(self)

        # --- source card ---------------------------------------------------
        src = QGroupBox("Data card / export folder")
        g = QGridLayout(src)
        self.card_edit = QLineEdit()
        self.card_edit.setPlaceholderText(
            "Folder from the machine (e.g. a USB card, or an ISOXML TASKDATA folder)")
        browse = QPushButton("Browse…")
        browse.clicked.connect(self._browse_card)
        self.detect_btn = QPushButton("Detect format")
        self.detect_btn.clicked.connect(self._detect)
        g.addWidget(QLabel("Folder:"), 0, 0)
        g.addWidget(self.card_edit, 0, 1)
        g.addWidget(browse, 0, 2)
        g.addWidget(self.detect_btn, 0, 3)
        self.detect_label = QLabel("Not checked yet.")
        self.detect_label.setWordWrap(True)
        g.addWidget(self.detect_label, 1, 0, 1, 4)
        layout.addWidget(src)

        # --- settings ------------------------------------------------------
        # Collapsible, and collapsed by default: an internal build ships with the
        # licensed plugins already in place, so most users never open this.
        cfg = _make_group("Settings")
        cg = QGridLayout(cfg)
        self.sms_edit = QLineEdit(sms)
        self.sms_edit.setPlaceholderText("Ag Leader SMS install folder (supplies the vendor plugins)")
        sms_btn = QPushButton("…")
        sms_btn.setFixedWidth(30)
        sms_btn.clicked.connect(self._browse_sms)
        self.exe_edit = QLineEdit(exe)
        self.exe_edit.setPlaceholderText("smsimport executable")
        exe_btn = QPushButton("…")
        exe_btn.setFixedWidth(30)
        exe_btn.clicked.connect(self._browse_exe)
        self.jd_edit = QLineEdit(s.value(f"{SETTINGS_PREFIX}/plugin_dir", ""))
        self.jd_edit.setPlaceholderText(
            "Licensed vendor plugin folder, e.g. John Deere ADAPT SDK release (optional)")
        jd_btn = QPushButton("…")
        jd_btn.setFixedWidth(30)
        jd_btn.clicked.connect(self._browse_jd)
        self.appid_edit = QLineEdit(s.value(f"{SETTINGS_PREFIX}/app_id", ""))
        self.appid_edit.setPlaceholderText("Vendor application id (optional)")

        cg.addWidget(QLabel("SMS install:"), 0, 0)
        cg.addWidget(self.sms_edit, 0, 1)
        cg.addWidget(sms_btn, 0, 2)
        cg.addWidget(QLabel("Sidecar:"), 1, 0)
        cg.addWidget(self.exe_edit, 1, 1)
        cg.addWidget(exe_btn, 1, 2)
        cg.addWidget(QLabel("Vendor plugins:"), 2, 0)
        cg.addWidget(self.jd_edit, 2, 1)
        cg.addWidget(jd_btn, 2, 2)
        cg.addWidget(QLabel("Application id:"), 3, 0)
        cg.addWidget(self.appid_edit, 3, 1)
        layout.addWidget(cfg)

        # --- results -------------------------------------------------------
        res = QGroupBox("Layers found")
        rl = QVBoxLayout(res)
        self.table = QTableWidget(0, 5)
        self.table.setHorizontalHeaderLabels(["Add", "Layer", "Points", "Channels", "Field / Operation"])
        self.table.horizontalHeader().setSectionResizeMode(1, QHeaderView.Stretch)
        self.table.setSelectionMode(QAbstractItemView.NoSelection)
        rl.addWidget(self.table)
        opts = QHBoxLayout()
        self.style_cb = QCheckBox("Apply yield styling")
        self.style_cb.setChecked(True)
        self.skip_small_cb = QCheckBox("Skip layers under 50 points")
        self.skip_small_cb.setChecked(True)
        self.nonzero_cb = QCheckBox("Show only non-zero readings")
        self.nonzero_cb.setChecked(True)
        self.nonzero_cb.setToolTip(
            "Raw cards log zeros on headland turns and in transport. Filtering "
            "them out is what makes a yield map readable.")
        opts.addWidget(self.style_cb)
        opts.addWidget(self.nonzero_cb)
        opts.addWidget(self.skip_small_cb)
        opts.addStretch(1)
        rl.addLayout(opts)
        layout.addWidget(res, stretch=1)

        # --- progress + buttons --------------------------------------------
        self.progress = QProgressBar()
        self.progress.setRange(0, 0)
        self.progress.setVisible(False)
        layout.addWidget(self.progress)

        self.status = QLabel("")
        self.status.setWordWrap(True)
        layout.addWidget(self.status)

        btns = QDialogButtonBox()
        self.import_btn = btns.addButton("Import", QDialogButtonBox.AcceptRole)
        self.add_btn = btns.addButton("Add selected to map", QDialogButtonBox.ApplyRole)
        close_btn = btns.addButton(QDialogButtonBox.Close)
        self.import_btn.clicked.connect(self._import)
        self.add_btn.clicked.connect(self._add_layers)
        close_btn.clicked.connect(self.reject)
        self.add_btn.setEnabled(False)
        layout.addWidget(btns)

    # -- helpers -----------------------------------------------------------

    def _sidecar(self) -> Sidecar:
        return Sidecar(self.exe_edit.text().strip(), self.sms_edit.text().strip(),
                       self.jd_edit.text().strip(), self.appid_edit.text().strip())

    def _save_settings(self):
        s = QgsSettings()
        s.setValue(f"{SETTINGS_PREFIX}/sms_dir", self.sms_edit.text().strip())
        s.setValue(f"{SETTINGS_PREFIX}/sidecar", self.exe_edit.text().strip())
        s.setValue(f"{SETTINGS_PREFIX}/plugin_dir", self.jd_edit.text().strip())
        s.setValue(f"{SETTINGS_PREFIX}/app_id", self.appid_edit.text().strip())

    def _browse_card(self):
        d = QFileDialog.getExistingDirectory(self, "Select the data card folder",
                                             self.card_edit.text())
        if d:
            self.card_edit.setText(d)
            self.detect_label.setText("Not checked yet.")

    def _browse_sms(self):
        d = QFileDialog.getExistingDirectory(self, "Select the SMS install folder",
                                             self.sms_edit.text())
        if d:
            self.sms_edit.setText(d)

    def _browse_jd(self):
        d = QFileDialog.getExistingDirectory(self, "Select the licensed vendor plugin folder",
                                             self.jd_edit.text())
        if d:
            self.jd_edit.setText(d)

    def _browse_exe(self):
        f, _ = QFileDialog.getOpenFileName(self, "Select the smsimport executable",
                                           self.exe_edit.text())
        if f:
            self.exe_edit.setText(f)

    # -- actions -----------------------------------------------------------

    def _detect(self):
        card = self.card_edit.text().strip()
        if not os.path.isdir(card):
            self.detect_label.setText("Pick an existing folder first.")
            return
        self._save_settings()
        try:
            self._detected = self._sidecar().detect(card)
        except SidecarError as exc:
            self.detect_label.setText(f"<b>Error:</b> {exc}")
            return
        if self._detected:
            names = ", ".join(p.label for p in self._detected)
            self.detect_label.setText(f"<b>Detected:</b> {names}")
        else:
            self.detect_label.setText(
                "<b>No installed reader recognises this folder.</b> "
                "Point at the card's root (the folder written by the display), "
                "not a subfolder. Note SMS's own internal Vault is not readable — "
                "use the original card or export."
            )

    def _import(self):
        card = self.card_edit.text().strip()
        if not os.path.isdir(card):
            QMessageBox.warning(self, "Import", "Pick an existing folder first.")
            return
        self._save_settings()

        # Deliberately NOT the system temp folder: the layers added to the map
        # reference this GeoPackage, so a cleaned temp directory would silently
        # break saved projects. Keep it under the user's documents instead.
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        out_dir = _output_dir()
        out = os.path.join(out_dir, f"smslibre_{stamp}.gpkg")

        plugin = self._detected[0].name if self._detected else None
        self.progress.setVisible(True)
        self.import_btn.setEnabled(False)
        self.status.setText("Importing… this can take a few minutes for a full card.")

        self._worker = _ImportWorker(self._sidecar(), card, out, plugin)
        self._worker.finished_ok.connect(self._on_imported)
        self._worker.failed.connect(self._on_failed)
        self._worker.start()

    def _on_failed(self, msg: str):
        self.progress.setVisible(False)
        self.import_btn.setEnabled(True)
        self.status.setText("")
        QMessageBox.critical(self, "Import failed", msg)

    def _on_imported(self, data: dict):
        self.progress.setVisible(False)
        self.import_btn.setEnabled(True)
        self._result = data
        layers = data.get("layers", [])
        self.table.setRowCount(0)

        if not layers:
            # The sidecar knows why it found nothing — a folder of prescription
            # maps, say — and saying so is the difference between a useful answer
            # and one that reads like the plugin is broken.
            self.status.setText(data.get("note")
                                or "Imported, but no spatial operations were found on this card.")
            return

        threshold = 50 if self.skip_small_cb.isChecked() else 0
        for row, lyr in enumerate(layers):
            self.table.insertRow(row)
            cb = QCheckBox()
            cb.setChecked(lyr.get("points", 0) >= threshold)
            holder = QWidget()
            h = QHBoxLayout(holder)
            h.addWidget(cb)
            h.setAlignment(Qt.AlignCenter)
            h.setContentsMargins(0, 0, 0, 0)
            self.table.setCellWidget(row, 0, holder)
            cb.setProperty("table_name", lyr.get("table", ""))

            self.table.setItem(row, 1, QTableWidgetItem(lyr.get("table", "")))
            self.table.setItem(row, 2, QTableWidgetItem(f"{lyr.get('points', 0):,}"))
            self.table.setItem(row, 3, QTableWidgetItem(str(len(lyr.get("channels", [])))))
            desc = " / ".join(x for x in (lyr.get("field", ""), lyr.get("operationType", "")) if x)
            self.table.setItem(row, 4, QTableWidgetItem(desc))

        total = sum(l.get("points", 0) for l in layers)
        msg = f"Imported {len(layers)} layer(s), {total:,} points → {data.get('geopackage','')}"
        # Anything the importer had to discard is said out loud rather than
        # left to be discovered as a hole in the map.
        rejected = data.get("rejectedPoints") or 0
        dropped = data.get("droppedChannels") or 0
        if rejected:
            msg += f"  ({rejected:,} point(s) dropped: implausible GPS fix)"
        if dropped:
            msg += f"  ({dropped:,} channel(s) dropped: layer wider than GeoPackage allows)"
        self.status.setText(msg)
        self.add_btn.setEnabled(True)

    def _add_layers(self):
        if not self._result:
            return
        gpkg = self._result.get("geopackage", "")
        added = 0
        styled = 0
        for row in range(self.table.rowCount()):
            holder = self.table.cellWidget(row, 0)
            cb = holder.findChild(QCheckBox) if holder else None
            if not cb or not cb.isChecked():
                continue
            table = cb.property("table_name")
            layer = QgsVectorLayer(f"{gpkg}|layername={table}", table, "ogr")
            if not layer.isValid():
                continue
            if self.style_cb.isChecked() and apply_yield_style(
                    layer, exclude_zero=self.nonzero_cb.isChecked()):
                styled += 1
            QgsProject.instance().addMapLayer(layer)
            added += 1

        if added:
            self.iface.messageBar().pushSuccess(
                "SMSLIBRE", f"Added {added} layer(s) to the map ({styled} styled).")
            self.status.setText(f"Added {added} layer(s).")
        else:
            self.status.setText("No layers selected.")
