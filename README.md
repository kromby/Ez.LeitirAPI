# Ez.Leitir

A thin, opinionated REST facade over the [leitir.is](https://www.leitir.is) Primo VE API. Returns clean, predictable JSON for the use cases the underlying API makes painful: search suggestions, keyword/ISBN search, and book detail with public-library branch availability aggregated across FRBR-related editions.

Built as a .NET 10 Azure Functions isolated worker.

## Endpoints

All endpoints require `X-Api-Key: <LEITIR_API_KEY>`.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/suggest?q=&scope=` | Search-as-you-type suggestions |
| GET | `/api/search?q=&scope=&offset=` | Keyword/ISBN search. Returns books split into `Available[]` (in public libraries) and `OnLoan[]`. |
| GET | `/api/book/{mmsId}` | Book detail with per-branch availability across all editions of the work |

### `/api/book/{mmsId}` shape

```json
{
  "Book": {
    "MmsId": "991016596577906886",
    "Title": "Kennarinn sem hvarf",
    "Author": "Bergrún Íris Sævarsdóttir",
    "Year": 2019,
    "Isbn": "...",
    "CoverSources": ["https://..."],
    "BranchesOnShelf": ["Bókasafn Kópavogs aðalsafn", "Borgarbókasafnið Árbæ", "..."],
    "Genres": [...],
    "Summary": "...",
    "PageCount": null,
    "EarliestReturn": null
  },
  "Branches": [
    { "Branch": "Bókasafn Akraness", "Status": "on-shelf", "CallNumber": "B Ber Ken", "EarliestReturn": null },
    { "Branch": "Bókasafn Fáskrúðsfjarðar", "Status": "on-loan", "CallNumber": "Ber Ken", "EarliestReturn": null }
  ]
}
```

`Status` is one of `on-shelf`, `on-loan`, `unavailable`. `EarliestReturn` is currently always null — due dates aren't disclosed to anonymous guest tokens by Primo.

## How `/api/book/{mmsId}` works

Primo represents the same work as multiple records (one per format/printing) joined by a FRBR group id. To answer "which public libraries have this book?" the function does:

1. **Consortium lookup** — `GET /primaws/rest/pub/pnxs/L/{mmsId}` → book metadata + `pnx.facets.frbrgroupid`
2. **Sibling lookup** — `GET /primaws/rest/pub/pnxs?qInclude=facet_frbrgroupid,exact,{frbrId}&...` → all sibling mmsIds
3. **Per-edition holdings** — for each sibling, `GET /primaws/rest/priv/nz/pnx/P/{mmsId}?record-institution=354ILC_ALM` in parallel → `delivery.holding[]` with real branches
4. **Aggregate** — flatten holdings, dedupe by branch name, keep best status (`on-shelf` > `on-loan` > `unavailable`)

Worst case ≈ N+2 upstream calls per request, dominated by the slowest of the N parallel edition lookups.

## Configuration

Set in `local.settings.json` (local) or Function App settings (deployed):

| Variable | Default | Purpose |
|---|---|---|
| `LEITIR_BASE_URL` | `https://www.leitir.is` | API host |
| `LEITIR_VID` | `354ILC_NETWORK:10000_UNION` | Primo view id |
| `LEITIR_INST` | `354ILC_NETWORK` | Consortium institution code |
| `LEITIR_SCOPE` | `10000_MYLIB` | Search scope |
| `LEITIR_INSTITUTION_FILTER` | `354ILC_ALM` | Institution whose branches to surface (public libraries by default) |
| `LEITIR_API_KEY` | — | Required. Validated against incoming `X-Api-Key`. |
| `ALLOWED_ORIGINS` | — | Comma-separated CORS allowlist |

## Running locally

```sh
dotnet build
func start
```

Smoke-test with the requests in [`test.http`](./test.http) (works with VS Code REST Client / JetBrains HTTP Client).

## Layout

```
Functions/    HTTP triggers (Suggest, Search, Book)
Services/     LeitirClient (upstream calls), LeitirJwtCache (guest JWT, proactive refresh)
Shaping/      LeitirShaper — transforms raw PNX into the response models
Models/       Response records
Middleware/   API-key validation
```

## Notes on the leitir.is API

A few non-obvious things learned the hard way:

- `getPhysicalService` is a stub — returns `{ physicalServiceId, link: [] }` and never has holdings. The real branch data lives in `delivery.holding[]`, but only when called with an **institution-specific** `record-institution` (e.g., `354ILC_ALM`). The consortium code (`354ILC_NETWORK`) gives metadata without holdings.
- FRBR sibling search uses `qInclude=facet_frbrgroupid,exact,{id}` — **not** `facet=frbrgroupid,include,{id}`. The latter is frontend URL state and is silently ignored by the API.
- `POST /delivery` 500s unless `qInclude=` and `qExclude=` are present (even empty), and rejects non-Alma record ids (e.g. `cdi_crossref_*`) with the same 500.
- The `/delivery` body is a **bare JSON array** of mmsIds (`["alma...","alma..."]`), not `{ "mmsIds": [...] }`.
