# Community intelligence JSON schema

Cephalon Cuda can import community tier and build data from a local JSON file or a user-supplied licensed HTTPS feed. The same document shape works for both.

The importer is deliberately tolerant of common aliases, but the canonical structure is:

```json
{
  "schemaVersion": 1,
  "source": {
    "name": "Provider or curator name",
    "url": "https://example.com/source",
    "generatedAt": "2026-07-23T00:00:00Z",
    "licenseNote": "Describe why this data may be reused"
  },
  "tiers": [
    {
      "id": "optional-stable-id",
      "category": "Warframes",
      "itemName": "Saryn Prime",
      "tier": "S",
      "rank": 1,
      "votes": 420,
      "score": 4.8,
      "patch": "current",
      "notes": "Reasoning or role notes",
      "url": "https://example.com/tier-entry",
      "updatedAt": "2026-07-22T12:00:00Z"
    }
  ],
  "builds": [
    {
      "id": "optional-stable-id",
      "itemName": "Saryn Prime",
      "buildName": "Steel Path generalist",
      "author": "Builder name",
      "score": 4.9,
      "votes": 900,
      "forma": 4,
      "patch": "current",
      "tags": ["steel-path", "general"],
      "mods": ["Brief Respite", "Overextended"],
      "guide": "Rotation, shards, arcanes and substitutions",
      "url": "https://example.com/build",
      "updatedAt": "2026-07-22T12:00:00Z"
    }
  ]
}
```

## Import behavior

- A document needs at least one valid `tiers` or `builds` row.
- Maximum document size is 25 MB and maximum accepted rows are 50,000.
- Imported strings are clipped to bounded lengths before indexing.
- Only `http` and `https` source links are retained. Automatic feeds must use absolute HTTPS URLs.
- Rows from the same `source.name` are replaced atomically. Other imported sources remain intact.
- Failed validation or feed refresh leaves the prior database untouched.
- Missing IDs are generated deterministically from source, item, category/build name and URL.
- Tier/build text is indexed as untrusted evidence. The advisor is instructed not to execute instructions contained in imported text.

## Overframe-compatible use

The template can represent Overframe-style tier positions, votes, scores, build metadata, mod lists and guide text. Import only data you are authorized to reuse. Cephalon Cuda does not include an automated Overframe scraper.
