"""Import dialog: pick a card folder, detect the format, convert, add layers."""

from __future__ import annotations

import os
import tempfile
from datetime import datetime

from qgis.core import QgsProject, QgsSettings, QgsVectorLayer
from qgis.PyQt.QtCore import Qt, QThread, pyqtSignal
from qgis.PyQt.QtWidgets import (
    QAbstractItemView, QCheckBox, QComboBox, QDialog, QDialogButtonBox,
    QFileDialog, QGridLayout, QGroupBox, QHBoxLayout, QHeaderView, QLabel,
    QLineEdit, QMessageBox, QProgressBar, QPushButton, QTableWidget,
    QTableWidgetItem, QVBoxLayout, QWidget,
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


def _cell(text: str) -> QTableWidgetItem:
    item = QTableWidgetItem(text)
    item.setFlags(item.flags() & ~Qt.ItemFlag.ItemIsEditable)
    return item


def _num_cell(value: int) -> QTableWidgetItem:
    """A right-aligned cell that sorts numerically, not as text."""
    item = QTableWidgetItem()
    item.setData(Qt.ItemDataRole.DisplayRole, value)
    item.setTextAlignment(Qt.AlignmentFlag.AlignRight
                          | Qt.AlignmentFlag.AlignVCenter)
    item.setFlags(item.flags() & ~Qt.ItemFlag.ItemIsEditable)
    return item


def _day(start: str | None, end: str | None) -> str:
    """The day a job ran, or the span when it crossed midnight."""
    if not start:
        return ""
    first, last = start[:10], (end or start)[:10]
    return first if first == last else f"{first} … {last}"


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

        # A full card is routinely 100+ jobs, so the list needs narrowing before
        # it needs ticking. Filters hide rows; they never change what is ticked,
        # so a selection survives changing your mind about the filter.
        flt = QHBoxLayout()
        self.filter_edit = QLineEdit()
        self.filter_edit.setPlaceholderText(
            "Filter by layer, field, farm or grower…")
        self.filter_edit.setClearButtonEnabled(True)
        self.filter_edit.textChanged.connect(self._apply_filter)
        self.op_combo = QComboBox()
        self.op_combo.addItem("All operations", "")
        self.op_combo.currentIndexChanged.connect(self._apply_filter)
        self.date_combo = QComboBox()
        self.date_combo.addItem("All dates", "")
        self.date_combo.currentIndexChanged.connect(self._apply_filter)
        flt.addWidget(QLabel("Filter:"))
        flt.addWidget(self.filter_edit, stretch=1)
        flt.addWidget(self.op_combo)
        flt.addWidget(self.date_combo)
        rl.addLayout(flt)

        self.table = QTableWidget(0, 6)
        # Enums are written in their scoped form throughout — Qt6 (QGIS 4)
        # removed the unscoped aliases, and Qt5 accepts the scoped form,
        # so one spelling serves both.
        self.table.setHorizontalHeaderLabels(
            ["Add", "Layer", "Points", "Channels", "Field / Operation", "When"])
        self.table.horizontalHeader().setSectionResizeMode(
            1, QHeaderView.ResizeMode.Stretch)
        # Rows are selectable so shift-click takes a range and ctrl-click adds
        # one. Ticking is a separate act from selecting: you select the rows you
        # mean, then tick them all at once.
        self.table.setSelectionMode(
            QAbstractItemView.SelectionMode.ExtendedSelection)
        self.table.setSelectionBehavior(
            QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setSortingEnabled(True)
        self.table.itemChanged.connect(self._on_item_changed)
        rl.addWidget(self.table)

        # Selection actions, above the display options so the two do not read as
        # one row of unrelated switches.
        sel = QHBoxLayout()
        self.count_label = QLabel("")
        for text, slot, tip in (
            ("Tick selected", lambda: self._set_selected(True),
             "Tick every highlighted row (shift-click for a range, "
             "ctrl-click to add one)"),
            ("Untick selected", lambda: self._set_selected(False),
             "Untick every highlighted row"),
            ("Tick all shown", lambda: self._set_visible(True),
             "Tick every row the filter is currently showing"),
            ("Untick all", lambda: self._set_visible(False),
             "Untick every row the filter is currently showing"),
        ):
            b = QPushButton(text)
            b.setToolTip(tip)
            b.clicked.connect(slot)
            sel.addWidget(b)
        sel.addStretch(1)
        sel.addWidget(self.count_label)
        rl.addLayout(sel)

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
        self.import_btn = btns.addButton("Import", QDialogButtonBox.ButtonRole.AcceptRole)
        self.add_btn = btns.addButton("Add selected to map", QDialogButtonBox.ButtonRole.ApplyRole)
        close_btn = btns.addButton(QDialogButtonBox.StandardButton.Close)
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
        # Sorting and change signals off while filling: each insert would
        # otherwise re-sort under us and fire a callback per cell.
        self.table.setSortingEnabled(False)
        self.table.blockSignals(True)
        for row, lyr in enumerate(layers):
            self.table.insertRow(row)

            # A checkable item rather than a checkbox widget: a widget in a cell
            # is invisible to the selection model, which is what makes
            # shift-click and keyboard selection work at all.
            tick = QTableWidgetItem()
            tick.setFlags((tick.flags() | Qt.ItemFlag.ItemIsUserCheckable)
                          & ~Qt.ItemFlag.ItemIsEditable)
            tick.setCheckState(
                Qt.CheckState.Checked if lyr.get("points", 0) >= threshold
                else Qt.CheckState.Unchecked)
            tick.setData(Qt.ItemDataRole.UserRole, lyr.get("table", ""))
            self.table.setItem(row, 0, tick)

            self.table.setItem(row, 1, _cell(lyr.get("table", "")))
            # Sort by the number, not by the string: "9,000" must not come
            # after "10,000" because "9" sorts after "1".
            self.table.setItem(row, 2, _num_cell(lyr.get("points", 0)))
            self.table.setItem(row, 3, _num_cell(len(lyr.get("channels", []))))
            desc = " / ".join(x for x in (lyr.get("field", ""), lyr.get("operationType", "")) if x)
            self.table.setItem(row, 4, _cell(desc))
            self.table.setItem(row, 5, _cell(_day(lyr.get("start"), lyr.get("end"))))
        self.table.blockSignals(False)
        self.table.setSortingEnabled(True)
        self.table.resizeColumnsToContents()
        # The layer name is the identifier people scan for, so it gets the
        # leftover width — but sizing every column to its contents first can
        # leave it none. Cap the descriptive columns so it always has room.
        for col, cap in ((4, 200), (5, 150)):   # 150 fits a full ISO date
            self.table.setColumnWidth(col, min(self.table.columnWidth(col), cap))
        self.table.horizontalHeader().setSectionResizeMode(
            1, QHeaderView.ResizeMode.Stretch)

        self._fill_filter_choices(layers)
        self._apply_filter()

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

    # -- filtering and bulk ticking ---------------------------------------

    def _fill_filter_choices(self, layers):
        """Offer only the operations and days this card actually contains."""
        ops = sorted({l.get("operationType", "") for l in layers if l.get("operationType")})
        days = sorted({(l.get("start") or "")[:10] for l in layers if l.get("start")})

        for combo, values, label in ((self.op_combo, ops, "All operations"),
                                     (self.date_combo, days, "All dates")):
            combo.blockSignals(True)
            combo.clear()
            combo.addItem(f"{label} ({len(values)})" if values else label, "")
            for v in values:
                combo.addItem(v, v)
            combo.setEnabled(bool(values))
            combo.blockSignals(False)

    def _row_matches(self, row: int) -> bool:
        text = self.filter_edit.text().strip().lower()
        op = self.op_combo.currentData() or ""
        day = self.date_combo.currentData() or ""

        if op and op not in (self.table.item(row, 4).text() if self.table.item(row, 4) else ""):
            return False
        if day and not (self.table.item(row, 5).text() if self.table.item(row, 5) else "").startswith(day):
            return False
        if text:
            # Search the columns a person would search by: the layer name and
            # the field/operation description.
            hay = " ".join(
                (self.table.item(row, c).text() if self.table.item(row, c) else "")
                for c in (1, 4, 5)).lower()
            if text not in hay:
                return False
        return True

    def _apply_filter(self):
        shown = 0
        for row in range(self.table.rowCount()):
            ok = self._row_matches(row)
            self.table.setRowHidden(row, not ok)
            shown += ok
        self._update_counts(shown)

    def _update_counts(self, shown: int | None = None):
        if shown is None:
            shown = sum(1 for r in range(self.table.rowCount())
                        if not self.table.isRowHidden(r))
        ticked = sum(1 for r in range(self.table.rowCount())
                     if (i := self.table.item(r, 0))
                     and i.checkState() == Qt.CheckState.Checked)
        total = self.table.rowCount()
        self.count_label.setText(
            f"{shown} of {total} shown · {ticked} ticked")

    def _on_item_changed(self, _item):
        self._update_counts()

    def _set_rows(self, rows, checked: bool):
        state = Qt.CheckState.Checked if checked else Qt.CheckState.Unchecked
        self.table.blockSignals(True)
        for row in rows:
            item = self.table.item(row, 0)
            if item:
                item.setCheckState(state)
        self.table.blockSignals(False)
        self._update_counts()

    def _set_selected(self, checked: bool):
        rows = {i.row() for i in self.table.selectedIndexes()}
        if not rows:
            self.status.setText(
                "Select rows first — click one, then shift-click another for a "
                "range, or ctrl-click to add individual rows.")
            return
        self._set_rows(sorted(rows), checked)

    def _set_visible(self, checked: bool):
        rows = [r for r in range(self.table.rowCount())
                if not self.table.isRowHidden(r)]
        self._set_rows(rows, checked)

    def _add_layers(self):
        if not self._result:
            return
        gpkg = self._result.get("geopackage", "")
        added = 0
        styled = 0
        for row in range(self.table.rowCount()):
            item = self.table.item(row, 0)
            if not item or item.checkState() != Qt.CheckState.Checked:
                continue
            table = item.data(Qt.ItemDataRole.UserRole)
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
