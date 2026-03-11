# DWG Rendering Improvement Plan

## Goal

Make `ACadSharp` and `ACadSharp.Pdf` production-grade for internal DWG verification workflows:

- reliable DWG/DXF ingestion for real-world architectural and site-plan drawings;
- focused rendering of arbitrary drawing fragments by handle or window;
- stable PDF export for human verification;
- low-noise diagnostics where non-graphical metadata is not relevant to visual verification;
- high-signal diagnostics where rendering correctness can actually be affected.

## Current State

The stack is already strong for the target verification workflow:

- focused model-window rendering works on the production DWG audit target;
- render reports for the target drawing are clean (`notifications = 0`);
- Unicode and Cyrillic text no longer degrade into question marks in verification PDFs;
- dynamic-block pass-through noise has been reduced;
- stream reading reliability has been improved via exact-read semantics;
- regression coverage exists for focused previews, text output, and dynamic-block handling.

## Newly Confirmed Problems From AutoCAD Differential Check

The latest comparison against an AutoCAD-exported verification PDF for the production cartogram drawing exposed several concrete defects that now take precedence over the broader roadmap items below.

### P0. PDF content streams are still invalid for external readers

Confirmed on the production DWG:

- AutoCAD-exported PDF renders correctly in MuPDF/PyMuPDF;
- `ACadSharp.Pdf` scene-graph export opens with `zlib error: unknown compression method`;
- `ACadSharp.Pdf` legacy export opens with `zlib error: incorrect header check`;
- both files only decompress correctly as **raw deflate**, while the PDF dictionaries advertise `/Filter /FlateDecode`.

Implication:

- current PDF output cannot be treated as a reliable verification artifact outside the library's own assumptions;
- any visual diff against AutoCAD is blocked until writer correctness is fixed.

Primary suspect:

- `Core/PdfContent.cs` currently writes raw `DeflateStream` bytes under `/FlateDecode`.

Required fix:

1. emit PDF-compliant zlib-wrapped Flate streams;
2. add regression tests that open exported PDFs with an external reader;
3. verify both scene-graph and legacy pipelines against MuPDF/PyMuPDF.

### P0. Full-page model preview extents are polluted by outlier geometry

Confirmed on the same production DWG:

- automatic full-model extents expand to absurd coordinates (`x ~= 17.6M`, `y ~= -7.9M`);
- the resulting denominator scale is meaningless for human verification;
- full-page preview is therefore not comparable to the published AutoCAD sheet/PDF.

Implication:

- default model-space preview currently fails as a trustworthy verification view for large production drawings with stray entities or broken extents.

Primary suspect:

- `ACadSharp.Pdf.Examples/Program.cs` merges all finite entity bounding boxes without rejecting outliers or offering a robust "main cluster" selection strategy.

Required fix:

1. add robust extents selection for model preview;
2. detect and reject obvious outlier clusters;
3. report why entities were excluded from preview extents;
4. keep explicit `--window` and focused-handle mode as deterministic override paths.

### P1. Differential validation must distinguish published-sheet output from raw model-space audit

The AutoCAD PDF used as ground truth is a published verification artifact, not merely an arbitrary model-space dump.

Confirmed observation:

- suspicious raw model-space text found in an earlier DWG audit does **not** appear in the published AutoCAD PDF;
- therefore "object exists somewhere in DWG" is not equivalent to "user-visible publication defect".

Implication:

- validation needs two separate bars:
  - raw CAD audit correctness;
  - publication/view correctness.

Required fix:

1. formalize verification modes: `model-audit`, `publication-sheet`, `focused-window`;
2. compare like-for-like windows/views only;
3. avoid reporting hidden/model-only artifacts as sheet-level visual defects unless they affect the published output.

### P1. External-reader validation is now mandatory, not optional

The AutoCAD differential check proved that internal success criteria are insufficient.

Required rule:

- every "production-ready" PDF export path must be validated by at least one external consumer such as MuPDF/PyMuPDF;
- "file exists and our report is clean" is not enough.

Minimum acceptance checks:

1. exported PDF opens without parser errors;
2. first page rasterizes successfully;
3. text extraction returns expected visible labels for a known fixture;
4. focused and full-page outputs both pass.

## Remaining Improvement Areas

### 1. Reader Correctness

Implement or deepen support for classes and structures that still rely on partial or pass-through handling:

- dynamic block action/parameter variants beyond the currently covered set;
- object context data edge cases;
- proxy-backed non-graphical objects that can affect downstream semantics;
- complex evaluation graph relationships.

Priority:

1. classes that can affect visible geometry;
2. classes that can affect text, dimensions, clipping, or draw order;
3. purely non-graphical metadata.

### 2. Rendering Completeness

Continue closing remaining visual gaps in the scene-graph path:

- spline fidelity and edge-case rendering;
- leader and multileader corner cases;
- block expansion edge cases with nested transforms and clipping;
- tolerance and dimension visual parity improvements;
- draw-order-sensitive content and masking behavior;
- proxy graphics fallback, where available.

### 3. Focused Verification UX

Further improve focused verification outputs:

- stronger clipping/culling outside focused windows;
- robust preview extents selection for full-model exports;
- more efficient reuse of repeated block content in PDF output;
- optional tile rendering for large drawings and audit grids;
- better reporting around what was intentionally skipped and why.

### 4. Differential Validation

Build a repeatable comparison harness against external references:

- compare against open-source readers/renderers where legally and technically useful;
- compare render artifacts against trusted CAD outputs where available;
- maintain a corpus of real production DWG/DXF files with expected behavior.

References may include:

- LibreDWG;
- libdxfrw;
- ezdxf for DXF behavior and text/layout expectations;
- trusted CAD export outputs for visual comparison.

The goal is not code copying by default, but behaviorally correct independent implementations informed by references.

### 5. Diagnostics Quality

Maintain a strict distinction between:

- high-value diagnostics that indicate possible render loss or semantic corruption;
- low-value diagnostics produced by benign pass-through metadata.

Rules:

- never suppress diagnostics that may hide visible rendering defects;
- allow silent pass-through only for classes proven irrelevant to the verification image;
- back each suppression rule with regression tests.

### 6. Reliability and Performance

Continue hardening runtime behavior:

- exact reads for all stream paths that assume full-buffer delivery;
- improved memory behavior on very large drawings;
- reduced PDF size for focused exports with repeated blocks;
- stable behavior across `net48`, `net8.0`, and `net9.0`.

## Execution Strategy

### Phase 1: High-Value Reader and Render Gaps

- fix PDF-compliant stream compression first;
- add external-reader regression coverage for generated PDFs;
- fix default full-page preview extents selection;
- identify remaining classes with verification impact;
- implement missing parsing/render logic;
- add targeted unit and integration tests;
- verify against the existing production DWG audit scenario.

### Phase 2: Differential Testing

- assemble a reusable corpus of DWG/DXF samples;
- render the same logical windows through external references;
- compare geometry presence, text fidelity, and export diagnostics.

### Phase 3: Performance and Export Quality

- reduce focused PDF size further;
- reuse repeated block content where possible;
- improve window clipping and scene-graph flattening costs.

### Phase 4: Long-Tail Cleanup

- reduce build warnings that mask real issues;
- clean up XML docs and obsolete usage where it improves maintainability;
- keep warning cleanup secondary to reader/render correctness.

## Definition of Done for Internal Production Use

For the internal verification workflow, the stack is considered production-ready when:

- generated PDFs open and rasterize in external readers without compression/parser errors;
- default preview extents stay within the intended drawing cluster or explicitly report outlier rejection;
- target real-world DWGs render without warnings/errors in focused verification mode;
- focused PDFs preserve readable text and critical geometry;
- reports clearly distinguish rendered vs intentionally skipped content;
- comparisons against trusted published outputs are done view-to-view, not merely object-to-object;
- no known rendering regressions exist in automated tests for covered scenarios;
- stream handling and export logic are robust under large real-world files.

## Non-Goals

This plan does not assume universal 100% compatibility with every DWG produced by every AutoCAD vertical product.

Instead, it targets:

- professional-grade correctness for internal architectural/genplan verification;
- transparent diagnostics for unsupported edge cases;
- continuous expansion of supported real-world coverage.
