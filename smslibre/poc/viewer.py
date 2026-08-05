"""PySide6/Qt viewer for the yield map.

Embeds a Matplotlib canvas (with the standard pan/zoom toolbar) in a Qt window
and adds a small controls panel: yield-class count and a Save PNG button. This
is the seed of the eventual native Linux UI that replaces SMS's WPF front end.
"""

from __future__ import annotations

import sys

from matplotlib.backends.backend_qtagg import (
    FigureCanvasQTAgg as FigureCanvas,
    NavigationToolbar2QT as NavigationToolbar,
)
from matplotlib.figure import Figure
from PySide6 import QtWidgets

from .yieldmap import FieldData, render


class YieldMapWindow(QtWidgets.QMainWindow):
    def __init__(self, fd: FieldData, *, n_classes: int = 7, units: str = ""):
        super().__init__()
        self.fd = fd
        self.units = units
        self.setWindowTitle(f"SMSLIBRE — Yield Map — {fd.name}")
        self.resize(1180, 820)

        self.figure = Figure(figsize=(11, 8.5))
        self.canvas = FigureCanvas(self.figure)
        toolbar = NavigationToolbar(self.canvas, self)

        # Controls row.
        controls = QtWidgets.QHBoxLayout()
        controls.addWidget(QtWidgets.QLabel("Yield classes:"))
        self.class_spin = QtWidgets.QSpinBox()
        self.class_spin.setRange(3, 12)
        self.class_spin.setValue(n_classes)
        self.class_spin.valueChanged.connect(self._redraw)
        controls.addWidget(self.class_spin)
        controls.addStretch(1)
        save_btn = QtWidgets.QPushButton("Save PNG…")
        save_btn.clicked.connect(self._save)
        controls.addWidget(save_btn)

        central = QtWidgets.QWidget()
        layout = QtWidgets.QVBoxLayout(central)
        layout.addWidget(toolbar)
        layout.addWidget(self.canvas, stretch=1)
        layout.addLayout(controls)
        self.setCentralWidget(central)

        self.statusBar().showMessage(
            f"{len(fd.yield_pts):,} points · {fd.area_ha:.1f} ha "
            f"({fd.area_ac:.1f} ac) · {fd.metric_crs}"
        )
        self._redraw()

    def _redraw(self):
        self.figure.clear()
        ax = self.figure.add_subplot(111)
        render(self.fd, ax, n_classes=self.class_spin.value(), units=self.units)
        self.figure.tight_layout()
        self.canvas.draw_idle()

    def _save(self):
        path, _ = QtWidgets.QFileDialog.getSaveFileName(
            self, "Save yield map", f"{self.fd.name}_yieldmap.png",
            "PNG image (*.png)")
        if path:
            self.figure.savefig(path, dpi=150, bbox_inches="tight")
            self.statusBar().showMessage(f"Saved {path}", 5000)


def launch(fd: FieldData, *, n_classes: int = 7, units: str = "") -> int:
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv)
    win = YieldMapWindow(fd, n_classes=n_classes, units=units)
    win.show()
    return app.exec()
