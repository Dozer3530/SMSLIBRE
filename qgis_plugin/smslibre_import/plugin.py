"""QGIS plugin entry point: toolbar button + menu item."""

from __future__ import annotations

import os

from qgis.PyQt.QtGui import QIcon
from qgis.PyQt.QtWidgets import QAction

from .dialog import ImportDialog

MENU = "&SMSLIBRE"


class SmslibreImportPlugin:
    def __init__(self, iface):
        self.iface = iface
        self.actions: list[QAction] = []

    def initGui(self):
        icon_path = os.path.join(os.path.dirname(__file__), "icon.svg")
        icon = QIcon(icon_path) if os.path.exists(icon_path) else QIcon()

        action = QAction(icon, "Import machine data…", self.iface.mainWindow())
        action.setToolTip(
            "Import yield / as-applied / as-planted data from a machine data card")
        action.triggered.connect(self.run)
        self.iface.addToolBarIcon(action)
        self.iface.addPluginToMenu(MENU, action)
        self.actions.append(action)

    def unload(self):
        for action in self.actions:
            self.iface.removePluginMenu(MENU, action)
            self.iface.removeToolBarIcon(action)
        self.actions.clear()

    def run(self):
        dlg = ImportDialog(self.iface, self.iface.mainWindow())
        dlg.exec_() if hasattr(dlg, "exec_") else dlg.exec()
