"""Stage 5 proof of concept: import one field boundary and render its yield map.

Deliberately narrow. It reads two standard ESRI Shapefiles — a field boundary
polygon and a cleaned yield point layer — and produces an SMS-style yield map,
either as a PNG (headless) or in a PySide6/Qt window. No SMS native code, no
WPF, no database: it proves the data pipeline end to end on open formats.
"""
