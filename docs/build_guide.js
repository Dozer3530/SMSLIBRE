/**
 * Build the SMSLIBRE User Guide (.docx).
 *
 *   node docs/build_guide.js
 *
 * Everything factual here is drawn from the code and from the committed sweep
 * results; the screenshots are captures of the real dialog and the map is
 * rendered from a GeoPackage this project produced.
 */

const fs = require("fs");
const path = require("path");
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType,
  Table, TableRow, TableCell, WidthType, ShadingType, BorderStyle,
  ImageRun, PageBreak, LevelFormat, PageOrientation, TabStopType,
  Header, Footer, PageNumber, ExternalHyperlink,
} = require("docx");

const HERE = __dirname;
const IMG = path.join(HERE, "images");
const SIZES = JSON.parse(fs.readFileSync(path.join(HERE, "image_sizes.json"), "utf8"));

const CONTENT_W = 9360;              // Letter (12240) minus 1" margins
const INK = "1A1A1A";
const ACCENT = "2F6F4E";
const HEAD_BG = "E8F1EC";
const ALT_BG = "F5F7F6";
const WARN_BG = "FBF0EE";
const NOTE_BG = "EEF3F7";

/* ---------- small builders ------------------------------------------- */

const H1 = (t) => new Paragraph({ text: t, heading: HeadingLevel.HEADING_1,
  spacing: { before: 360, after: 160 }, pageBreakBefore: true });
const H2 = (t) => new Paragraph({ text: t, heading: HeadingLevel.HEADING_2,
  spacing: { before: 280, after: 120 } });
const H3 = (t) => new Paragraph({ text: t, heading: HeadingLevel.HEADING_3,
  spacing: { before: 220, after: 100 } });

const P = (text, opts = {}) => new Paragraph({
  children: [new TextRun({ text, size: 21, ...opts })],
  spacing: { after: 130, line: 288 },
});

/** Rich paragraph: pass an array of {text, bold, italics, code} chunks. */
const RP = (chunks, opts = {}) => new Paragraph({
  children: chunks.map((c) =>
    new TextRun({
      text: c.text,
      bold: !!c.bold,
      italics: !!c.italics,
      size: 21,
      ...(c.code ? { font: "Consolas", size: 19 } : {}),
      ...(c.color ? { color: c.color } : {}),
    })),
  spacing: { after: 130, line: 288 },
  ...opts,
});

const BULLET = (text, level = 0) => new Paragraph({
  children: [new TextRun({ text, size: 21 })],
  numbering: { reference: "bullets", level },
  spacing: { after: 70, line: 276 },
});

const STEP = (text) => new Paragraph({
  children: [new TextRun({ text, size: 21 })],
  numbering: { reference: "steps", level: 0 },
  spacing: { after: 90, line: 276 },
});

/** Monospace block, shaded, for commands and file trees. */
const CODE = (lines) => new Table({
  width: { size: CONTENT_W, type: WidthType.DXA },
  columnWidths: [CONTENT_W],
  rows: [new TableRow({
    children: [new TableCell({
      width: { size: CONTENT_W, type: WidthType.DXA },
      shading: { type: ShadingType.CLEAR, fill: "F4F4F2" },
      margins: { top: 120, bottom: 120, left: 160, right: 160 },
      children: lines.map((l) => new Paragraph({
        children: [new TextRun({ text: l, font: "Consolas", size: 18 })],
        spacing: { after: 20 },
      })),
    })],
  })],
});

/** Callout box: kind is "note" | "warn". */
const CALLOUT = (kind, title, lines) => new Table({
  width: { size: CONTENT_W, type: WidthType.DXA },
  columnWidths: [CONTENT_W],
  rows: [new TableRow({
    children: [new TableCell({
      width: { size: CONTENT_W, type: WidthType.DXA },
      shading: { type: ShadingType.CLEAR, fill: kind === "warn" ? WARN_BG : NOTE_BG },
      margins: { top: 140, bottom: 140, left: 180, right: 180 },
      borders: {
        left: { style: BorderStyle.SINGLE, size: 18,
                color: kind === "warn" ? "A4392F" : ACCENT },
      },
      children: [
        new Paragraph({
          children: [new TextRun({ text: title, bold: true, size: 21,
            color: kind === "warn" ? "A4392F" : ACCENT })],
          spacing: { after: 80 },
        }),
        ...lines.map((l) => new Paragraph({
          children: [new TextRun({ text: l, size: 20 })],
          spacing: { after: 60, line: 276 },
        })),
      ],
    })],
  })],
});

/** Table from a header row plus body rows; widths are fractions of CONTENT_W. */
const TABLE = (headers, rows, fractions) => {
  const widths = fractions.map((f) => Math.round(CONTENT_W * f));
  const diff = CONTENT_W - widths.reduce((a, b) => a + b, 0);
  widths[widths.length - 1] += diff;                  // must sum exactly

  const cell = (text, i, opts = {}) => new TableCell({
    width: { size: widths[i], type: WidthType.DXA },
    shading: { type: ShadingType.CLEAR, fill: opts.fill || "FFFFFF" },
    margins: { top: 80, bottom: 80, left: 120, right: 120 },
    // Monospace cells are paths and file trees, where the line breaks are
    // meaningful. Prose is joined and left to wrap: hard-wrapping it in the
    // source snapped sentences mid-clause at whatever width was guessed here.
    children: (opts.mono
      ? String(text).split("\n")
      : [String(text).replace(/\n/g, " ")]
    ).map((line) => new Paragraph({
      children: [new TextRun({
        text: line,
        bold: !!opts.bold,
        size: 19,
        ...(opts.mono ? { font: "Consolas", size: 17 } : {}),
      })],
      spacing: { after: 20 },
    })),
  });

  return new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    columnWidths: widths,
    rows: [
      new TableRow({
        tableHeader: true,
        children: headers.map((h, i) => cell(h, i, { bold: true, fill: HEAD_BG })),
      }),
      ...rows.map((r, ri) => new TableRow({
        children: r.map((c, i) => cell(c, i, {
          fill: ri % 2 ? ALT_BG : "FFFFFF",
          mono: typeof c === "string" && c.startsWith("`"),
        })),
      })),
    ],
  });
};

/** Figure with caption, scaled to the content width. */
let figureNo = 0;
const FIGURE = (name, caption, widthPx = 620) => {
  const [w, h] = SIZES[name];
  figureNo += 1;
  return [
    new Paragraph({
      children: [new ImageRun({
        type: "png",
        data: fs.readFileSync(path.join(IMG, `${name}.png`)),
        transformation: { width: widthPx, height: Math.round(widthPx * (h / w)) },
      })],
      alignment: AlignmentType.CENTER,
      spacing: { before: 120, after: 60 },
      keepNext: true,
    }),
    new Paragraph({
      children: [new TextRun({ text: `Figure ${figureNo}. ${caption}`,
        size: 18, italics: true, color: "5A5A5A" })],
      alignment: AlignmentType.CENTER,
      spacing: { after: 200 },
    }),
  ];
};

const SPACER = () => new Paragraph({ text: "", spacing: { after: 120 } });

/* ---------- document ------------------------------------------------- */

const children = [];

/* Title page */
children.push(
  new Paragraph({ text: "", spacing: { after: 1800 } }),
  new Paragraph({
    children: [new TextRun({ text: "SMSLIBRE", bold: true, size: 76, color: ACCENT })],
    alignment: AlignmentType.CENTER, spacing: { after: 80 },
  }),
  new Paragraph({
    children: [new TextRun({ text: "Machine Data Import for QGIS", size: 34, color: INK })],
    alignment: AlignmentType.CENTER, spacing: { after: 400 },
  }),
  new Paragraph({
    children: [new TextRun({ text: "User Guide", size: 28, color: "5A5A5A" })],
    alignment: AlignmentType.CENTER, spacing: { after: 120 },
  }),
  new Paragraph({
    children: [new TextRun({ text: "Version 1.0.0", size: 22, color: "5A5A5A" })],
    alignment: AlignmentType.CENTER, spacing: { after: 1400 },
  }),
  new Paragraph({
    children: [new TextRun({
      text: "Olds College Centre for Innovation — Smart Farm",
      size: 22, color: INK })],
    alignment: AlignmentType.CENTER, spacing: { after: 60 },
  }),
  new Paragraph({
    children: [new TextRun({ text: "Internal tool — not for public distribution",
      size: 20, italics: true, color: "A4392F" })],
    alignment: AlignmentType.CENTER,
  }),
  new Paragraph({ children: [new PageBreak()] }),
);

/* Contents */
children.push(
  new Paragraph({ text: "Contents", heading: HeadingLevel.HEADING_1,
    spacing: { after: 200 } }),
  ...[
    ["1.", "What SMSLIBRE is"],
    ["2.", "Before you start"],
    ["3.", "Installing the plugin"],
    ["4.", "Your first import"],
    ["5.", "The dialog, control by control"],
    ["6.", "Choosing the right folder"],
    ["7.", "Supported formats"],
    ["8.", "Understanding what you get"],
    ["9.", "Working with the data in QGIS"],
    ["10.", "Troubleshooting"],
    ["11.", "Using the sidecar from the command line"],
    ["12.", "Testing a whole drive"],
    ["13.", "Building from source"],
    ["14.", "Licensing and distribution"],
    ["15.", "Quick reference"],
  ].map(([n, title]) => new Paragraph({
    children: [
      new TextRun({ text: n, size: 22, bold: true, color: ACCENT }),
      new TextRun({ text: "\t" + title, size: 22 }),
    ],
    tabStops: [{ type: TabStopType.LEFT, position: 620 }],
    spacing: { after: 140 },
  })),
  new Paragraph({ children: [new PageBreak()] }),
);

/* ===================== 1. What this is ============================== */
children.push(new Paragraph({ text: "1. What SMSLIBRE is",
  heading: HeadingLevel.HEADING_1, spacing: { after: 160 } }));

children.push(P(
  "SMSLIBRE is a QGIS plugin that reads precision-agriculture machine data — " +
  "yield monitor logs, as-applied spray records, as-planted seeding data, field " +
  "boundaries and variable-rate prescriptions — and turns them into map layers " +
  "you can work with directly in QGIS."));
children.push(P(
  "You point it at the folder your display wrote to a USB stick. It works out " +
  "which manufacturer's format that is, converts everything it finds into a " +
  "GeoPackage, and adds the layers to your map with sensible styling already " +
  "applied. Every sensor channel the machine logged becomes an attribute you " +
  "can query, filter, classify and export."));

children.push(H2("Why it exists"));
children.push(P(
  "Ag Leader SMS can read these cards, but it is Windows-only desktop software " +
  "and getting data out of it and into a GIS workflow is slow and manual. " +
  "SMSLIBRE puts the same import capability inside QGIS, where the analysis " +
  "actually happens."));

children.push(H2("How it fits together"));
children.push(P(
  "The vendor import libraries are .NET assemblies, so the plugin does not try " +
  "to load them into QGIS's Python. Instead it runs a small console program — " +
  "the \"sidecar\" — as a separate process and reads back a single JSON summary. " +
  "If a vendor library crashes on a malformed card, QGIS is unaffected."));
children.push(...FIGURE("architecture",
  "The plugin runs the readers in a separate process and gets a GeoPackage back.", 620));

children.push(H2("What has been tested"));
children.push(P(
  "Before release, every directory on both Smart Farm shared drives was taken " +
  "through the full pipeline — detect, import, open the GeoPackage, validate " +
  "every coordinate:"));
children.push(TABLE(
  ["Shared drive", "Directories walked", "Cards imported", "Features"],
  [
    ["Olds College Smart Farm Vault", "13,458", "553", "108,864,986"],
    ["210600 STAAR", "9,240", "82", "67,377,366"],
  ],
  [0.40, 0.20, 0.18, 0.22]));
children.push(SPACER());
children.push(P(
  "176 million features imported, with no invalid or out-of-range coordinates " +
  "surviving anywhere. Six cards fail, all for known reasons documented in " +
  "chapter 10 — none of them a format the plugin claims to support."));

/* ===================== 2. Before you start ========================== */
children.push(H1("2. Before you start"));

children.push(H2("What you need"));
children.push(TABLE(
  ["Requirement", "Detail"],
  [
    ["Windows", "Windows 10 or 11. The sidecar is built for win-x64."],
    ["QGIS", "3.22 or newer. Tested on QGIS 3.44 LTR and 4.0."],
    ["Disk space", "About 200 MB for the plugin. Imported GeoPackages can be large —\na very wide John Deere card can produce several GB."],
    ["The card", "The original folder written by the display, or a copy of it.\nSee chapter 6 — pointing at the wrong folder is the most common problem."],
  ],
  [0.22, 0.78]));

children.push(H2("Which build do you want?"));
children.push(P(
  "There are two builds and the difference matters. Both contain the same code; " +
  "they differ only in whether John Deere's licensed material is bundled."));
children.push(SPACER());
children.push(TABLE(
  ["Build", "File", "John Deere support", "Who it is for"],
  [
    ["Internal", "smslibre_import_INTERNAL.zip",
     "Works immediately — Deere's plugin release and our licence key are bundled",
     "Olds College staff. Never share outside the organisation."],
    ["Public", "smslibre_import.zip",
     "Not until you supply your own Deere ADAPT release and application id",
     "Anyone else, or a machine where the licence must not go."],
  ],
  [0.12, 0.26, 0.34, 0.28]));
children.push(SPACER());
children.push(CALLOUT("warn", "Distribution rule", [
  "The internal build contains material licensed to Olds College by John Deere. " +
  "The licence forbids redistributing it. Do not email it, do not attach it to " +
  "a GitHub release, do not put it on a public share. Chapter 14 has the detail.",
]));

/* ===================== 3. Installing =============================== */
children.push(H1("3. Installing the plugin"));

children.push(H2("Install from ZIP"));
children.push(STEP("Close QGIS completely if it is open. See the warning below."));
children.push(STEP("Start QGIS."));
children.push(STEP("From the menu choose Plugins → Manage and Install Plugins…"));
children.push(STEP("Select Install from ZIP on the left."));
children.push(STEP("Browse to smslibre_import_INTERNAL.zip (or the public zip) and choose Install Plugin."));
children.push(STEP("QGIS reports success. Close the plugin manager."));
children.push(SPACER());
children.push(P(
  "You now have a toolbar button and a menu entry under Plugins → SMSLIBRE → " +
  "Import machine data…"));

children.push(SPACER());
children.push(CALLOUT("warn", "Nothing may be holding the plugin folder", [
  "Installing replaces the whole plugin folder. If QGIS is open, or an import " +
  "is still running, some files are locked — the installer deletes everything " +
  "it can around them and you are left with a broken, partial install.",
  "Symptoms: the plugin fails to load, or an import that was running dies with " +
  "no message at all. Close QGIS first, and check Task Manager for a lingering " +
  "SmsImport.exe before reinstalling.",
]));

children.push(H2("Checking it worked"));
children.push(P(
  "Open the dialog from the toolbar. It should look like this, with the Sidecar " +
  "path already filled in under Settings. If the Sidecar box is empty, the " +
  "install did not complete — reinstall with QGIS closed."));
children.push(...FIGURE("dialog-1-empty",
  "The import dialog as it opens. Settings is collapsed because an internal " +
  "build needs no configuration.", 600));

children.push(H2("Where things end up"));
children.push(TABLE(
  ["What", "Where"],
  [
    ["The plugin",
     "`%APPDATA%\\QGIS\\QGIS3\\profiles\\default\\python\\plugins\\smslibre_import"],
    ["The sidecar", "`...\\smslibre_import\\bin\\SmsImport.exe"],
    ["Imported data", "`%USERPROFILE%\\Documents\\SMSLIBRE\\smslibre_<date>_<time>.gpkg"],
    ["Your settings", "QGIS settings, under the smslibre_import prefix"],
  ],
  [0.25, 0.75]));
children.push(SPACER());
children.push(CALLOUT("note", "Imported files are kept, not cleaned up", [
  "Layers on your map point at the GeoPackage in Documents\\SMSLIBRE. That folder " +
  "is deliberately not a temporary directory — deleting a file there breaks any " +
  "saved project that uses it. Tidy it up periodically, but check your projects first.",
]));

/* ===================== 4. First import ============================= */
children.push(H1("4. Your first import"));
children.push(P("Five steps, start to finish."));

children.push(H3("Step 1 — Open the dialog"));
children.push(P("Click the SMSLIBRE button on the toolbar, or Plugins → SMSLIBRE → Import machine data…"));

children.push(H3("Step 2 — Choose the card folder"));
children.push(P(
  "Click Browse… and select the folder from the machine. Pick the folder that " +
  "contains the display's folder — not the deepest folder with files in it. " +
  "Chapter 6 covers this properly; it is the one thing people get wrong."));

children.push(H3("Step 3 — Detect the format"));
children.push(P(
  "Click Detect format. The plugin asks every reader it has whether it " +
  "recognises the folder, and reports what it found."));
children.push(...FIGURE("dialog-2-detected",
  "A Raven Viper 4 job folder recognised. The name in brackets is who supplies " +
  "the reader — SMSLIBRE for our own, or the vendor for an ADAPT plugin.", 600));
children.push(P(
  "Detection is optional — you can press Import directly — but it is fast and " +
  "tells you immediately whether the folder is right."));

children.push(H3("Step 4 — Import"));
children.push(P(
  "Click Import. A progress bar appears and the dialog stays responsive; the " +
  "work happens in a background process. A small card takes seconds. A large " +
  "John Deere card with thousands of layers can take fifteen minutes or more, " +
  "and reading from a network drive is slower than from a local disk."));
children.push(...FIGURE("dialog-3-imported",
  "After import: every layer found, with its point count and channel count. " +
  "Layers under 50 points are unticked automatically.", 600));

children.push(H3("Step 5 — Add the layers you want"));
children.push(P(
  "Tick the layers you want and click Add selected to map. They are added with " +
  "styling already applied. The status line tells you where the GeoPackage was " +
  "written — note that path if you want to reopen the data later without " +
  "re-importing."));

/* ===================== 5. The dialog ============================== */
children.push(H1("5. The dialog, control by control"));

children.push(H2("Data card / export folder"));
children.push(TABLE(
  ["Control", "What it does"],
  [
    ["Folder", "The card folder to read. Type or paste a path, or use Browse…"],
    ["Browse…", "Opens a folder picker."],
    ["Detect format", "Asks the readers what they make of the folder and reports the answer.\nDoes not import anything."],
  ],
  [0.22, 0.78]));

children.push(H2("Settings"));
children.push(P(
  "Collapsed by default, because an internal build is already configured. It " +
  "remembers whether you left it open. Everything here is saved between sessions."));
children.push(...FIGURE("dialog-4-settings",
  "Settings expanded. On an internal build the first two are filled in " +
  "automatically and the last two are left blank on purpose.", 600));
children.push(TABLE(
  ["Setting", "What it is for"],
  [
    ["SMS install",
     "Your Ag Leader SMS folder. SMS ships the ADAPT plugin suite, and that is\nwhere the ISOXML, Climate, CNH and other vendor readers come from.\nFound automatically at C:\\Program Files\\Ag Leader Technology\\SMS."],
    ["Sidecar",
     "The SmsImport.exe that does the work. Filled in by the installer.\nOnly change this if you are testing a build you compiled yourself."],
    ["Vendor plugins",
     "A licensed vendor plugin release — in practice John Deere's ADAPT SDK\ndownload. Bundled in the internal build, so normally left blank."],
    ["Application id",
     "The GUID issued with a vendor licence. Bundled in the internal build."],
  ],
  [0.20, 0.80]));

children.push(H2("Layers found"));
children.push(P(
  "One row per layer the card produced. Layer names are built from a sequence " +
  "number, the field name and the operation type, so they sort in a sensible " +
  "order in the QGIS layer tree."));
children.push(TABLE(
  ["Column", "Meaning"],
  [
    ["Add", "Tick to add this layer to the map."],
    ["Layer", "The table name inside the GeoPackage."],
    ["Points", "How many logged records the layer holds."],
    ["Channels", "How many sensor channels were recorded — these become attribute columns."],
    ["Field / Operation", "The field name and operation type the machine recorded, when it did."],
  ],
  [0.16, 0.84]));

children.push(H2("The three options"));
children.push(TABLE(
  ["Option", "Default", "What it does"],
  [
    ["Apply yield styling", "On",
     "Picks the most meaningful numeric channel — yield first, then rates, then\nelevation — and applies a quantile red-to-green ramp. Without this a layer\nis a mass of identical dots."],
    ["Show only non-zero readings", "On",
     "Hides records where the styled value is zero. Machines log zeros on headland\nturns and during transport; leaving them in flattens the colour range and\nmakes a yield map unreadable."],
    ["Skip layers under 50 points", "On",
     "Unticks tiny layers. Cards routinely contain dozens of stub layers with a\nhandful of points that are not worth mapping."],
  ],
  [0.24, 0.10, 0.66]));

children.push(H2("The buttons"));
children.push(TABLE(
  ["Button", "What it does"],
  [
    ["Import", "Converts the card. Enabled once you have chosen a folder."],
    ["Add selected to map", "Adds the ticked layers. Enabled after a successful import."],
    ["Close", "Closes the dialog. The GeoPackage stays on disk — you can add it\nto a map later through Layer → Add Layer → Add Vector Layer."],
  ],
  [0.22, 0.78]));

/* ===================== 6. Choosing the folder ====================== */
children.push(H1("6. Choosing the right folder"));

children.push(CALLOUT("warn", "This is the single most common problem", [
  "\"The plugin cannot open my data\" is almost always the wrong folder selected, " +
  "not an unsupported card. The readers look for a specific structure and will " +
  "decline anything else — including the folder one level too deep.",
]));

children.push(P(
  "The rule: select the folder that CONTAINS the display's own folder. For a " +
  "John Deere GS3 card, the display writes a folder called GS3_2630; you select " +
  "its parent, not it, and not the RCD folder inside it."));
children.push(...FIGURE("card-structures",
  "Left: correct. Middle: too deep, nothing will be recognised. Right: a card " +
  "that lost its display folder in a copy — SMSLIBRE rebuilds it for you.", 620));

children.push(H2("What to select, by format"));
children.push(TABLE(
  ["If the card contains…", "Select…"],
  [
    ["`GS3_2630\\<client>\\RCD\\", "the folder above GS3_2630"],
    ["`JD-Data\\log\\*.jdl", "the folder above JD-Data"],
    ["`TASKDATA\\TASKDATA.XML", "the TASKDATA folder, or the folder above it"],
    ["`*.jdp files (Raven)", "the Jobs folder holding them"],
    ["`RCD\\ with no display folder above it", "the folder holding RCD — SMSLIBRE recovers it"],
    ["`*.zip files that are cards", "the folder holding the zips — they are unpacked for you"],
  ],
  [0.45, 0.55]));

children.push(H2("When you are not sure"));
children.push(P(
  "Use Detect format and work upward. Select a folder, press Detect, and if " +
  "nothing is recognised try its parent. Detection is quick and harmless. The " +
  "readers search downward from wherever you point them, so being one level too " +
  "high is usually fine — being too deep is not."));

children.push(CALLOUT("note", "SMS's own Vault is not readable", [
  "Ag Leader SMS keeps imported data in an internal Vault in its own format. " +
  "The vendor ADAPT plugins cannot read that, and neither can SMSLIBRE. Always " +
  "use the original card or a fresh export from the display.",
]));

/* ===================== 7. Formats ================================= */
children.push(H1("7. Supported formats"));

children.push(P(
  "SMSLIBRE has nine readers. Five come from the vendor ADAPT plugin suite that " +
  "ships with SMS; four were written for this project to handle cards the vendor " +
  "plugins decline."));

children.push(H2("What works"));
children.push(TABLE(
  ["Format", "Reader", "Notes"],
  [
    ["John Deere Gen4 (4600, 4640)", "ProtobufPlugins",
     "Needs the Deere licence. The most common card here; can be very large."],
    ["John Deere GS3 / GS4 (2630, 4600)", "RCDPlugins",
     "Needs the Deere licence. Reads the RCD folder structure."],
    ["ISOXML / ISO 11783", "ISO v4 Plugin",
     "No licence needed. Also the export route for AGCO, New Holland and others."],
    ["Raven Viper 4 / Viper 4+", "Raven Viper 4 job (.jdp)",
     "Written for this project. Track, elevation, speed, distance, applied and\ntarget rate, sections on, heading, cross-track error."],
    ["Raven Slingshot", "Raven Slingshot (.jdp.zip)",
     "Written for this project. No licence needed."],
    ["Ag Leader / ADM", "ADMPlugin", "No licence needed."],
    ["Cards inside a .zip", "Card in an archive",
     "Written for this project. Unpacks and imports, including cards with no\nunzipped copy anywhere."],
    ["Loose Gen4 .jdl logs", "John Deere Gen4 logs (.jdl)",
     "Written for this project. For logs copied out of their card folder."],
    ["Stranded RCD folders", "John Deere RCD folder",
     "Written for this project. For an RCD folder copied without its display folder."],
    ["ISOXML prescriptions", "(part of the ISOXML path)",
     "Variable-rate plans become polygon zones with product, rate and unit."],
    ["Field boundaries", "(any reader that finds them)",
     "Become a polygon layer named field_boundaries."],
  ],
  [0.28, 0.24, 0.48]));

children.push(H2("What does not work, and why"));
children.push(TABLE(
  ["Format", "Status", "What to do"],
  [
    ["Trimble AgData", "Licence refused",
     "The plugin loads but rejects our application id. A licence request is with\nTrimble. No workaround in the plugin."],
    ["New Holland / CNH Voyager2 (.cn1)", "Not readable",
     "The card's TASKDATA.XML is a 208-byte empty placeholder; the real data is in\nCNH's own files, which no ADAPT plugin reads. Export ISOXML from the display\ninstead — that imports perfectly."],
    ["Raven native jobs without TASKDATA", "Partially readable",
     "Track, speed and rates are read. Some job types store their logs in a form\nthat is not decoded."],
    ["Precision Planting", "Untested",
     "The plugin is present but no Precision Planting cards exist here to test it\nagainst. It claims some drone-imagery folders by mistake — see chapter 10."],
  ],
  [0.28, 0.16, 0.56]));

children.push(H2("Coverage in practice"));
children.push(P(
  "From the release sweep of the Smart Farm Vault, by reader:"));
children.push(...FIGURE("coverage-by-reader",
  "Features imported per reader across the Smart Farm Vault. John Deere formats " +
  "dominate by volume; the readers written for this project cover the long tail " +
  "of cards that had been moved, zipped or stripped of their structure.", 600));

/* ===================== 8. What you get ============================ */
children.push(H1("8. Understanding what you get"));

children.push(H2("One GeoPackage per import"));
children.push(P(
  "Every import writes a single .gpkg file to Documents\\SMSLIBRE, named with " +
  "the date and time. A GeoPackage is an OGC standard container — it is really " +
  "a SQLite database — and QGIS, ArcGIS, GDAL and R all read it natively. " +
  "Each layer from the card is a table inside it."));

children.push(H2("What a layer looks like"));
children.push(P("A logged operation layer has this shape:"));
children.push(CODE([
  "fid           INTEGER   row id",
  "geom          BLOB      the GPS point",
  "timestamp     TEXT      when the record was logged",
  "elevation     REAL      \\",
  "speed         REAL       |  one column per channel the machine",
  "rate_applied  REAL       |  recorded — anywhere from 3 to 1,500",
  "rate_target   REAL      /",
]));
children.push(SPACER());
children.push(P(
  "Channel names come from the machine, so they vary by manufacturer and " +
  "implement. A combine logs yield and moisture; a sprayer logs applied rate and " +
  "section states; a seeder logs population and down-force. Everything the card " +
  "recorded is kept — nothing is summarised away."));

children.push(H2("Other layer types"));
children.push(TABLE(
  ["Layer", "When it appears", "What is in it"],
  [
    ["`field_boundaries", "The card defines field boundaries",
     "Polygons with field, farm, grower and description"],
    ["`prescription_zones", "The card is a variable-rate plan",
     "Polygons with task, field, product, rate and unit"],
  ],
  [0.24, 0.32, 0.44]));

children.push(H2("A real example"));
children.push(P(
  "This is an imported Raven sprayer job, coloured by applied rate. The pale " +
  "green line at the top is the machine driving to the field with the boom off; " +
  "the block below is the actual spraying pattern; red is where the rate dropped " +
  "to zero. Nothing here was drawn by hand — it is one layer from one import, " +
  "styled by the plugin's own default."));
children.push(...FIGURE("map-track-rate",
  "One imported layer: 4,195 points of a real spray job, coloured by applied " +
  "rate in litres per hectare.", 400));

children.push(H2("When an import finds nothing"));
children.push(P(
  "An empty result is not necessarily a failure, and the plugin tells you which " +
  "it is. A prescription-only card, a setup card with just boundaries, or a " +
  "Voyager2 card with a placeholder TASKDATA each produce a specific message " +
  "rather than a bare \"nothing found\"."));

/* ===================== 9. Working in QGIS ========================= */
children.push(H1("9. Working with the data in QGIS"));

children.push(H2("What the automatic styling does"));
children.push(P(
  "With Apply yield styling ticked, the plugin looks through the layer's numeric " +
  "fields and picks the most meaningful one, preferring in order: yield volume " +
  "or mass per area, dry yield, harvest moisture, applied rate, target rate, " +
  "seed rate, then elevation. Configuration and status fields — offsets, widths, " +
  "latencies, flags — are never chosen. It then applies a quantile " +
  "classification with a red-to-green ramp, the convention for yield maps."));

children.push(H2("Changing what is displayed"));
children.push(P(
  "The automatic choice is a starting point. To map a different channel: " +
  "right-click the layer → Properties → Symbology, and change the Value field. " +
  "The classification stays; only the source column changes."));

children.push(H2("Filtering out noise"));
children.push(P(
  "If you left Show only non-zero readings off and the map looks flat, apply a " +
  "filter instead of re-importing. Right-click the layer → Filter… and use " +
  "something like:"));
children.push(CODE([
  '"Yield_Volume_Per_Area" > 0',
  '',
  '-- or a sensible agronomic range, to drop start-of-pass spikes',
  '"Yield_Volume_Per_Area" BETWEEN 1 AND 400',
]));

children.push(H2("Useful things to do next"));
children.push(BULLET("Compare a prescription against what was actually applied by loading prescription_zones under the as-applied layer."));
children.push(BULLET("Map applied rate against yield for the same field to see whether the extra input paid for itself."));
children.push(BULLET("Interpolate a yield surface: Processing → Interpolation → IDW, using the yield field."));
children.push(BULLET("Clip to a boundary with Vector → Geoprocessing Tools → Clip, using field_boundaries."));
children.push(BULLET("Export for a report: right-click the layer → Export → Save Features As…, choosing CSV, shapefile or GeoJSON."));
children.push(BULLET("Join several seasons on a common field to look at year-over-year variation."));

children.push(H2("Reopening data without re-importing"));
children.push(P(
  "The GeoPackage keeps everything. Layer → Add Layer → Add Vector Layer, browse " +
  "to the .gpkg in Documents\\SMSLIBRE, and QGIS lists every layer inside it. " +
  "Re-importing the same card is only necessary if you need it converted again."));

/* ===================== 10. Troubleshooting ======================== */
children.push(H1("10. Troubleshooting"));

children.push(H2("No installed reader recognises this folder"));
children.push(...FIGURE("dialog-5-not-recognised",
  "The message shown when detection finds nothing.", 600));
children.push(P("In order of likelihood:"));
children.push(BULLET("You selected a folder one level too deep. Try the parent — see chapter 6."));
children.push(BULLET("It is an SMS Vault folder rather than a card. Use the original card."));
children.push(BULLET("It is a Voyager2 (.cn1) card. Export ISOXML from the display instead."));
children.push(BULLET("It genuinely holds no machine data — documents, imagery or shapefiles."));

children.push(H2("Common messages and what they mean"));
children.push(TABLE(
  ["Message", "Cause", "What to do"],
  [
    ["Invalid license provided for 'Trimble AgData Plugin'",
     "The Trimble plugin will not accept our application id",
     "Nothing in the plugin fixes this. A licence request is pending."],
    ["Plugin.Initialize() must be called using your application id",
     "A John Deere plugin has no licence available",
     "You are on the public build. Use the internal build, or set Vendor\nplugins and Application id in Settings."],
    ["This card's TASKDATA is an empty placeholder written by CNH",
     "A New Holland / Voyager2 card",
     "Expected. Export ISOXML from the display."],
    ["No logged work here: N of M archive(s) hold a prescription",
     "The folder holds rate plans, not recorded work",
     "Expected. The prescription_zones layer has the plans in it."],
    ["Sidecar not found",
     "The plugin folder is incomplete",
     "Reinstall with QGIS closed and no SmsImport.exe running."],
    ["Sidecar produced no output",
     "The sidecar was killed, usually by a reinstall during an import",
     "Close QGIS, check for a stray SmsImport.exe, reinstall, try again."],
    ["Import timed out after 3600s",
     "A very large card on a slow network drive",
     "Copy the card to a local disk and import from there."],
  ],
  [0.30, 0.28, 0.42]));

children.push(H2("The import succeeded but the map looks wrong"));
children.push(TABLE(
  ["Symptom", "Explanation"],
  [
    ["Everything is one colour",
     "The styled channel is constant, or zeros dominate. Tick Show only non-zero\nreadings, or pick a different field in Symbology."],
    ["Points are scattered across the world",
     "This should no longer happen — corrupt GPS fixes are rejected on import.\nIf you see it, report it: it means a new failure mode."],
    ["A layer has far fewer channels than expected",
     "A GeoPackage table cannot exceed 1,999 columns. On extremely wide cards the\nchannels with the most readings are kept and the rest dropped; the status\nline says how many."],
    ["Layers named op01, op02, …",
     "The card recorded no field or operation name. The numbering keeps them\ndistinct and in order."],
  ],
  [0.32, 0.68]));

children.push(H2("Precision Planting errors on drone imagery"));
children.push(P(
  "The Precision Planting ADAPT plugin claims any folder whose name ends in SET " +
  "— which matches MicaSense drone capture folders such as SYNC0001SET — and then " +
  "fails with a null reference error. This is the vendor plugin misbehaving, not " +
  "your data. Ignore it, or move drone imagery out of folders you sweep."));

/* ===================== 11. Command line =========================== */
children.push(H1("11. Using the sidecar from the command line"));

children.push(P(
  "The sidecar is a normal console program. It is useful for scripting, for " +
  "batch conversions, and for diagnosing a card without opening QGIS. It prints " +
  "a single JSON object on stdout; progress goes to stderr."));

children.push(H2("The four commands"));
children.push(CODE([
  "smsimport plugins            --sms <smsInstallDir>",
  "smsimport detect <cardPath>  --sms <smsInstallDir>",
  "smsimport scan   <root>      --sms <smsInstallDir> [--depth N] [--max N] [--min-depth N]",
  "smsimport import <cardPath> <out.gpkg> --sms <smsInstallDir> [--plugin <name>]",
]));
children.push(SPACER());
children.push(TABLE(
  ["Command", "What it does"],
  [
    ["`plugins", "Lists every reader available, and why any failed to initialise.\nThe first thing to run when John Deere formats are not detected."],
    ["`detect", "Reports which readers claim a folder. Imports nothing."],
    ["`scan", "Walks a folder tree and reports every card it finds. Use --min-depth 2\non a large share: readers search recursively, so asking about the top of\na drive means walking the whole thing."],
    ["`import", "Converts a card to a GeoPackage."],
  ],
  [0.14, 0.86]));

children.push(H2("Examples"));
children.push(P("The executable lives inside the installed plugin:"));
children.push(CODE([
  'cd "%APPDATA%\\QGIS\\QGIS3\\profiles\\default\\python\\plugins\\smslibre_import\\bin"',
  "",
  ":: what readers do I have, and did any fail to load?",
  "SmsImport.exe plugins",
  "",
  ":: what is this folder?",
  'SmsImport.exe detect "D:\\Card\\MyField"',
  "",
  ":: convert it",
  'SmsImport.exe import "D:\\Card\\MyField" "%USERPROFILE%\\Documents\\myfield.gpkg"',
  "",
  ":: find every card on a drive",
  'SmsImport.exe scan "G:\\Shared drives\\My Drive" --depth 12 --min-depth 2',
]));

children.push(H2("Reading the output"));
children.push(P(
  "Every command returns JSON with an ok field. On success an import also " +
  "reports the layers written, and anything it had to discard — rejectedPoints " +
  "for implausible GPS fixes, droppedChannels where a layer exceeded the column " +
  "limit. Those numbers are worth checking on an unfamiliar card."));

/* ===================== 12. Bulk testing =========================== */
children.push(H1("12. Testing a whole drive"));

children.push(P(
  "tools/vault_test.py takes every directory under a root through the full " +
  "pipeline — discover, import, open the GeoPackage, validate every coordinate — " +
  "and writes both a machine-readable result file and a markdown coverage " +
  "report. This is how the release numbers in chapter 1 were produced."));

children.push(CODE([
  "python tools/vault_test.py \\",
  '  --root "G:/Shared drives/Olds College Smart Farm Vault" \\',
  "  --out analysis/vault \\",
  "  --depth 14 --cap 200000 --workers 5 --min-depth 2 \\",
  "  --timeout 7200 --scan-timeout 21600",
]));
children.push(SPACER());
children.push(TABLE(
  ["Option", "Why you would use it"],
  [
    ["`--min-depth 2", "Do not interrogate the top levels of a big share. Essential:\nwithout it a vault-wide scan can take hours before it starts."],
    ["`--reuse-scan", "Skip the discovery walk and reuse the last one. Use after changing\nimport code, since discovery's answer has not changed."],
    ["`--only-detected", "Re-import just the folders a reader already claimed."],
    ["`--resume", "Continue a run that was interrupted. Results are checkpointed\nevery ten cards."],
    ["`--workers N", "Parallel imports. Five is a reasonable ceiling on a network share."],
  ],
  [0.20, 0.80]));
children.push(SPACER());
children.push(P(
  "Two files are produced: results.json, which the regression test suite reads " +
  "as its corpus, and COVERAGE.md — a report of what imported, what did not, and " +
  "why, with unclaimed folders characterised by the file types they hold so a " +
  "format gap is distinguishable from a folder of PDFs."));

children.push(CALLOUT("note", "Budget the time", [
  "A full sweep of both Smart Farm drives takes about five and a half hours and " +
  "needs the network drive mounted throughout. Run it overnight.",
]));

/* ===================== 13. Building =============================== */
children.push(H1("13. Building from source"));

children.push(H2("What you need"));
children.push(BULLET(".NET 10 SDK"));
children.push(BULLET("Python 3.9 or newer"));
children.push(BULLET("Ag Leader SMS installed, for the vendor ADAPT plugins"));
children.push(BULLET("For the internal build: the secrets/ folder and vendor/jd-plugins/, which are not in source control"));

children.push(H2("Building"));
children.push(CODE([
  "git clone https://github.com/Dozer3530/SMSLIBRE.git",
  "cd SMSLIBRE",
  "",
  ":: public build — no licensed material",
  "python qgis_plugin/build_plugin.py --runtime win-x64",
  "",
  ":: internal build, and install it into QGIS in one step",
  "python qgis_plugin/build_plugin.py --runtime win-x64 --internal --install",
]));
children.push(SPACER());
children.push(P("Zips are written to build/. Close QGIS before using --install."));

children.push(H2("Running the tests"));
children.push(CODE([
  "dotnet test sidecar/tests/SmsLibre.Import.Tests -c Release",
  "",
  ":: skip the tests that need the shared drive",
  'dotnet test sidecar/tests/SmsLibre.Import.Tests -c Release --filter "Category!=Corpus"',
]));
children.push(SPACER());
children.push(P(
  "67 tests, none skipped when the shared drive is available. They cover the " +
  "coordinate rule, the column limit, every reader's claim logic, and a corpus " +
  "suite that re-imports a real card for each reader from the last sweep's " +
  "results. If the corpus tests skip, the drive is not mounted."));

children.push(CALLOUT("warn", "Rebuild the zip after any C# change", [
  "The sidecar is a compiled binary inside the plugin folder. Editing the C# " +
  "source and reloading the QGIS plugin changes nothing until you rebuild and " +
  "reinstall.",
]));

/* ===================== 14. Licensing ============================== */
children.push(H1("14. Licensing and distribution"));

children.push(CALLOUT("warn", "Read this before sharing anything", [
  "Olds College holds a John Deere SDK Licence for Display Plugins for ADAPT. " +
  "It permits internal use and prohibits redistribution.",
]));

children.push(H2("The rules"));
children.push(TABLE(
  ["You may", "You may not"],
  [
    ["Use the internal build on Olds College machines",
     "Send the internal build to anyone outside the organisation"],
    ["Share the public build freely",
     "Attach the internal build to a GitHub release, even a private one"],
    ["Share imported GeoPackages — your data is yours",
     "Publish the licensed plugin binaries or the licence key"],
    ["Build the internal zip yourself from the repo",
     "Reverse-engineer the licensed John Deere components"],
  ],
  [0.50, 0.50]));

children.push(H2("How the build enforces it"));
children.push(P(
  "The packager strips licensed material from the public zip even if it is " +
  "present in the working folder, and prints a warning when it produces an " +
  "internal build. The secrets and vendor plugin folders are excluded from git. " +
  "These are safety nets, not permission — the responsibility is yours."));

/* ===================== 15. Reference ============================== */
children.push(H1("15. Quick reference"));

children.push(H2("Paths"));
children.push(TABLE(
  ["What", "Path"],
  [
    ["Plugin folder", "`%APPDATA%\\QGIS\\QGIS3\\profiles\\default\\python\\plugins\\smslibre_import"],
    ["Sidecar", "`...\\smslibre_import\\bin\\SmsImport.exe"],
    ["Imported data", "`%USERPROFILE%\\Documents\\SMSLIBRE\\"],
    ["SMS install", "`C:\\Program Files\\Ag Leader Technology\\SMS"],
    ["Repository", "`https://github.com/Dozer3530/SMSLIBRE"],
  ],
  [0.22, 0.78]));

children.push(H2("Glossary"));
children.push(TABLE(
  ["Term", "Meaning"],
  [
    ["ADAPT", "AgGateway's open framework for agricultural data. Manufacturers publish\nplugins for it; SMS ships a set of them."],
    ["Card", "The folder a display writes to a USB stick, and by extension any copy of it."],
    ["Channel", "One sensor value logged per record — yield, moisture, rate, speed.\nBecomes an attribute column."],
    ["DDI", "Data Dictionary Identifier. The ISO 11783 code identifying what a\nchannel measures."],
    ["GeoPackage", "An OGC standard file format for spatial data. One file, many layers."],
    ["ISOXML", "The ISO 11783 exchange format. Vendor-neutral, and the best route for\nmachines whose native format is not supported."],
    ["Prescription", "A planned application rate map, made before the work is done."],
    ["Sidecar", "The separate console program that runs the readers."],
    ["TASKDATA", "The root XML file of an ISOXML card."],
  ],
  [0.18, 0.82]));

children.push(H2("Getting help"));
children.push(P(
  "Report problems at https://github.com/Dozer3530/SMSLIBRE/issues. A useful " +
  "report includes: what folder you selected, what Detect format said, the exact " +
  "message you saw, and — most usefully — the output of running " +
  "SmsImport.exe detect on the same folder from the command line."));

/* ---------- assemble -------------------------------------------------- */

const doc = new Document({
  creator: "Olds College Centre for Innovation",
  title: "SMSLIBRE User Guide",
  description: "Importing precision-agriculture machine data into QGIS",
  styles: {
    default: {
      document: { run: { font: "Calibri", size: 21, color: INK } },
    },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal",
        quickFormat: true,
        run: { size: 34, bold: true, color: ACCENT, font: "Calibri" },
        paragraph: { spacing: { before: 360, after: 160 } } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal",
        quickFormat: true,
        run: { size: 26, bold: true, color: INK, font: "Calibri" },
        paragraph: { spacing: { before: 280, after: 120 } } },
      { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal",
        quickFormat: true,
        run: { size: 23, bold: true, color: "3A3A3A", font: "Calibri" },
        paragraph: { spacing: { before: 220, after: 100 } } },
    ],
  },
  numbering: {
    config: [
      { reference: "bullets",
        levels: [
          { level: 0, format: LevelFormat.BULLET, text: "\u2022",
            alignment: AlignmentType.LEFT,
            style: { paragraph: { indent: { left: 460, hanging: 240 } } } },
          { level: 1, format: LevelFormat.BULLET, text: "\u25E6",
            alignment: AlignmentType.LEFT,
            style: { paragraph: { indent: { left: 900, hanging: 240 } } } },
        ] },
      { reference: "steps",
        levels: [
          { level: 0, format: LevelFormat.DECIMAL, text: "%1.",
            alignment: AlignmentType.LEFT,
            style: { paragraph: { indent: { left: 460, hanging: 260 } } } },
        ] },
    ],
  },
  sections: [{
    properties: {
      page: {
        size: { width: 12240, height: 15840, orientation: PageOrientation.PORTRAIT },
        margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 },
      },
    },
    headers: {
      default: new Header({
        children: [new Paragraph({
          children: [new TextRun({ text: "SMSLIBRE User Guide  ·  v1.0.0",
            size: 17, color: "8A8A8A" })],
          alignment: AlignmentType.RIGHT,
        })],
      }),
    },
    footers: {
      default: new Footer({
        children: [new Paragraph({
          children: [
            new TextRun({ text: "Olds College Centre for Innovation — internal  ·  page ",
              size: 17, color: "8A8A8A" }),
            new TextRun({ children: [PageNumber.CURRENT], size: 17, color: "8A8A8A" }),
          ],
          alignment: AlignmentType.CENTER,
        })],
      }),
    },
    children,
  }],
});

Packer.toBuffer(doc).then((buf) => {
  const out = path.join(HERE, "SMSLIBRE_User_Guide.docx");
  fs.writeFileSync(out, buf);
  console.log(`wrote ${out} (${Math.round(buf.length / 1024)} KB)`);
});
