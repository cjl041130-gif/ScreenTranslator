# English → Chinese Fast Offline Pack

This package is a small deterministic phrase and terminology model authored for
ScreenTranslator Phase 3. It runs entirely in process and makes no network
requests. The package is intended to validate the model lifecycle, terminology,
cache, layout and overlay pipeline with common presentation phrases.

It is not a general-purpose neural machine translation model. Text outside the
bundled phrase and vocabulary tables is preserved and marked as a partial
translation so the product never presents unknown text as a completed result.

Files:

- `metadata.json`: language pair, version and runtime metadata.
- `config.json`: tokenizer and fallback behavior.
- `tokenizer/rules.json`: local normalization rules.
- `vocabulary.json`: word-level fallback vocabulary.
- `model/phrase-table.json`: deterministic phrase table.

Data origin: project-authored test vocabulary. No third-party model weights or
corpora are included.
