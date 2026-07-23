# Data pipeline

Cephalon Cuda stores public intelligence in SQLite and retrieves only query-relevant records for the advisor.

## Sources and refresh strategy

| Source | Typical refresh | Failure behavior |
|---|---:|---|
| WFCD items | 24 hours | preserve last valid cache |
| WFCD official drops | 24 hours | transactional rollback and stale cache |
| warframe.market catalog/snapshot | source-specific TTL | cached snapshot and retry status |
| named-item live orders | on relevant query | cached metric fallback |
| relic data | 6 hours | retain memory cache |
| Mastery catalog | 24 hours | retry in 1 hour |
| licensed community feed | 12 hours | retry in 1 hour, never replace on malformed input |
| world state | service timer | last snapshot plus visible error |

The user-configurable 15–120 minute loop is a scheduler wake-up interval, not a forced redownload. Each source independently decides whether it is due.

## Integrity controls

- response-size limits
- minimum record thresholds
- JSON shape validation
- ETag and Last-Modified validators
- compressed HTTP responses and connection pooling
- bounded retries and polite market pacing
- database transaction replacement
- content hashing for unchanged drop datasets
- source status, last success, next refresh, URL, and error state
- FTS5 indexing with a LIKE fallback
- community text marked as untrusted evidence before advisor use
