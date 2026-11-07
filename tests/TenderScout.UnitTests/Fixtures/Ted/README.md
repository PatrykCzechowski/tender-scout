# TED API fixtures

Responses recorded from `POST https://api.ted.europa.eu/v3/notices/search` (TED API v3) on 2026-11-07, for the
query used by `TedClient` (buyer country `POL`, main CPV `72*`/`48*`, publication date 2026-10-20).

To keep them small, the recordings were trimmed:

- `notice-title` keeps only `pol` and `eng`; `links` keeps only `xml` and `html` in `POL`/`ENG`/`MUL`.
- `search-page-{1,2,3}.json`: five recorded notices split into pages of 2/2/1 with the real iteration tokens;
  `totalNoticeCount` is set to 5 to match. TED returns a token even on the last page, as page 3 does.
- `search-missing-fields.json`: two recorded notices with optional fields removed or emptied by hand
  to cover the mapping fallbacks.
- `error-unknown-field.json`: the real HTTP 400 body for an expert query with an unknown field.
