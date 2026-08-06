"""SMSLIBRE Machine Data Import — QGIS plugin.

Imports precision-agriculture machine data (John Deere, Climate, Precision
Planting, Trimble, CNH, ISOXML) into QGIS by driving the AgGateway ADAPT plugin
suite through a small .NET sidecar, keeping every logged sensor channel as a
layer attribute.
"""


def classFactory(iface):  # noqa: N802 — required QGIS entry-point name
    from .plugin import SmslibreImportPlugin
    return SmslibreImportPlugin(iface)
