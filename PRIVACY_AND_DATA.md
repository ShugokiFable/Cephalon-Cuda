# Privacy and data handling

Cephalon Cuda is local-first.

It does not read Warframe memory, inject DLLs, automate controls, or submit game actions. It may read the local `EE.log`, capture user-selected game visuals for OCR, and access public web data.

Stored under `%LocalAppData%\CephalonCuda`:

- SQLite database
- source caches and freshness metadata
- settings
- encrypted API secrets
- logs and verification reports
- extracted embedded advisor assets

OpenRouter requests contain the question and compact relevant context selected for the advisor. Users should review the configured model provider’s privacy policy before adding an API key.

No personal database, API key, market account data, screenshots, or logs are included in public/source releases.
