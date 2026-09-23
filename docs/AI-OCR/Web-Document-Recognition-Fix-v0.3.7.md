# ScreenTranslator v0.3.7 — Web document recognition fix

## Problem reproduced

The real portrait online document was previously treated as the entire browser body. The OCR crop was 2542×1273 and included editor toolbars, side workspace and taskbar pixels. Small browser changes repeatedly canceled active OCR and DeepSeek requests, so the user normally saw only a green frame.

## Changes

- Added portrait/landscape paper detection for browsers, online documents and PDF viewers.
- Added an online Word content inset so the floating editor ribbon does not enter OCR.
- Kept full browser-content fallback for ordinary web pages without a paper surface.
- Added a separate 650 ms stability gate for browser/PDF documents.
- Discarded transient caret/hover fingerprints when the original document image returns.
- Routed clean web/PDF document text through the on-device Windows English OCR fast path; PaddleOCR remains the high-accuracy path for presentations and images and the fallback when Windows OCR finds no text.
- Kept web-document lines in strict top-to-bottom order before paragraph merging and whole-page DeepSeek translation.

## Actual-document evidence

- Detected page: 1222×1059, aspect ratio 1.154.
- Page confidence: 0.954.
- OCR time: 168 ms in the final order test; 160 ms in the repeated fast-path run.
- OCR lines: 29.
- AI context blocks after paragraph merging: 8.
- The actual document English text, wrapped-line continuity and reading order checks passed.

## Regression

- Vision browser/PDF and portrait-page suite passed.
- Phase 3 OCR/translation/state suite passed: 90 checks.
- Real published-product test passed on a local Chrome document: OCR 152.7 ms, DeepSeek 4317.5 ms, total 4501.5 ms, HTTP 200, 8 translated context blocks.
- The live overlay window exposed all 8 Chinese results through Windows UI Automation. Its window affinity remains `WDA_EXCLUDEFROMCAPTURE`, so translation text intentionally does not appear in screenshots or in ScreenTranslator's own capture frames.
- 30-second GPU capture regression passed at about 28.4 preview FPS, including 8 restart cycles, Pause/Resume/Stop, resource release and 125%/150%/175% DPI checks.
- Protected Windows Graphics Capture files were not modified.
