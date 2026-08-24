"""Automatic styling for imported machine-data layers.

A raw yield layer is unreadable as uniform dots, so we pick the most meaningful
numeric channel and apply a quantile-classified red→yellow→green ramp — the
convention for yield maps.
"""

from __future__ import annotations

from qgis.PyQt.QtCore import Qt
from qgis.core import (
    QgsClassificationQuantile,
    QgsFillSymbol,
    QgsGraduatedSymbolRenderer,
    QgsPalLayerSettings,
    QgsSingleSymbolRenderer,
    QgsStyle,
    QgsSymbol,
    QgsTextFormat,
    QgsVectorLayer,
    QgsVectorLayerSimpleLabeling,
    QgsWkbTypes,
)

# Preferred value channels, most meaningful first. Matched case-insensitively as
# substrings against the layer's field names.
PREFERRED_CHANNELS = (
    "yield_volume_per_area",
    "yield_mass_per_area",
    "average_dry_yield",
    "dry_yield",
    "yield_vol",
    "yield",
    "harvest_moisture",
    # Both spellings: ADAPT plugins emit "applied_rate", the Raven Viper reader
    # emits "rate_applied". Matching is a substring test, so one does not cover
    # the other — and without this a sprayer layer styled by elevation, which is
    # not what anyone opens a spray map to see.
    "rate_applied",
    "applied_rate",
    "rate_target",
    "target_rate",
    "seed_rate",
    "elevation",
)

# Channels that are flags/config rather than measurements — never style by these.
_SKIP = ("status", "offset", "width", "latency", "delay", "compensation",
         "hold", "engaged", "type", "count", "index", "id")


def pick_value_field(layer: QgsVectorLayer) -> str | None:
    """Choose the field that best represents 'the value' for this layer."""
    numeric = []
    for f in layer.fields():
        if f.typeName().lower() in ("real", "double", "integer", "integer64", "int"):
            numeric.append(f.name())
        elif f.type() in (2, 4, 6):  # int, longlong, double
            numeric.append(f.name())
    if not numeric:
        return None

    lowered = {n.lower(): n for n in numeric}

    for want in PREFERRED_CHANNELS:
        for low, orig in lowered.items():
            if want in low and not any(s in low for s in _SKIP):
                if _has_variation(layer, orig):
                    return orig

    # Fall back to the first numeric field that actually varies.
    for n in numeric:
        if n.lower() in ("fid",) or any(s in n.lower() for s in _SKIP):
            continue
        if _has_variation(layer, n):
            return n
    return None


def style_boundary(layer: QgsVectorLayer) -> str:
    """Hollow outline + field-name labels, so boundaries sit over data layers."""
    symbol = QgsFillSymbol.createSimple({
        "style": "no",                 # transparent fill
        "outline_color": "31,120,180,255",
        "outline_width": "0.6",
        "outline_style": "solid",
    })
    layer.setRenderer(QgsSingleSymbolRenderer(symbol))

    label_field = next(
        (n for n in ("field", "description", "farm")
         if layer.fields().indexOf(n) >= 0), None)
    if label_field:
        settings = QgsPalLayerSettings()
        settings.fieldName = label_field
        fmt = QgsTextFormat()
        fmt.setSize(9)
        settings.setFormat(fmt)
        settings.placement = QgsPalLayerSettings.Placement.Horizontal \
            if hasattr(QgsPalLayerSettings, "Placement") else 1
        layer.setLabeling(QgsVectorLayerSimpleLabeling(settings))
        layer.setLabelsEnabled(True)

    layer.triggerRepaint()
    return label_field or "boundary"


def _has_variation(layer: QgsVectorLayer, field: str) -> bool:
    """A constant or all-null column makes a useless map."""
    idx = layer.fields().indexOf(field)
    if idx < 0:
        return False
    vmin = layer.minimumValue(idx)
    vmax = layer.maximumValue(idx)
    if vmin is None or vmax is None:
        return False
    try:
        return float(vmax) > float(vmin)
    except (TypeError, ValueError):
        return False


def apply_yield_style(layer: QgsVectorLayer, classes: int = 7,
                      ramp_name: str = "RdYlGn",
                      exclude_zero: bool = True) -> str | None:
    """Graduated-quantile style the layer. Returns the field used, or None.

    Raw machine logs contain many zero readings — headland turns, transport,
    header raised — and they dominate the low quantiles, collapsing several
    classes onto 0 and making the map meaningless. With ``exclude_zero`` the
    layer is filtered to real readings before classifying, which is what a yield
    map should show.
    """
    # Boundaries are outlines, not measurements: hollow fill + a name label.
    if layer.geometryType() == QgsWkbTypes.PolygonGeometry:
        return style_boundary(layer)

    field = pick_value_field(layer)
    if not field:
        return None

    if exclude_zero:
        clause = f'"{field}" > 0'
        prior = layer.subsetString()
        layer.setSubsetString(f"({prior}) AND {clause}" if prior else clause)

    symbol = QgsSymbol.defaultSymbol(layer.geometryType())
    symbol.setSize(1.2)
    try:
        # No outline: dense point clouds turn into solid ink otherwise. Qt6
        # rejects the bare 0 that Qt5 accepted for a pen style, and the except
        # below would have swallowed that into outlines silently coming back.
        symbol.symbolLayer(0).setStrokeStyle(Qt.PenStyle.NoPen)
    except Exception:
        pass

    style = QgsStyle.defaultStyle()
    ramp = style.colorRamp(ramp_name) if ramp_name in style.colorRampNames() else None

    renderer = QgsGraduatedSymbolRenderer(field)
    renderer.setClassificationMethod(QgsClassificationQuantile())
    renderer.setSourceSymbol(symbol)
    if ramp is not None:
        renderer.setSourceColorRamp(ramp)
    renderer.updateClasses(layer, classes)
    if ramp is not None:
        renderer.updateColorRamp(ramp)

    layer.setRenderer(renderer)
    layer.triggerRepaint()
    return field
