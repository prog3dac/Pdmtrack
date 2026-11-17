# Pdmtrack

PDM (Product Data Management) per la gestione di file e archivi SolidWorks: articoli, distinte (BOM), where-used, check-in/check-out, versioni, workflow di rilascio e modifiche ECO.

## Stato

Fase 1 in corso: database PostgreSQL (`db/`), API REST ASP.NET Core (`api/`) e interfaccia web in sola lettura (`web/`). Il prototipo cliccabile originale resta in `prototype/index.html`.

## Database

Schema PostgreSQL 16 in `db/migrations/`, dati di esempio in `db/seed.sql`, dettagli e note per un futuro passaggio a Supabase in [`db/README.md`](db/README.md).

- Tabelle: `items`, `item_revisions`, `files`, `file_versions`, `file_refs`, `bom_lines`, `item_events`, `ecos`, `eco_items`, `eco_approvals`, `app_users`.
- Funzioni: `add_file`, `check_out`, `check_out_many`, `check_in`, `undo_check_out`, `force_unlock`, `transition_item`, `approve_eco`, `bom_explode` (distinta multilivello), `item_mass`, `version_tree` e `revision_tree` (file e versioni esatte da scaricare), `next_revision`; vista `where_used`.
- L'utente si ricava da `current_app_user()`, mai da un parametro; le scritture passano solo dalle funzioni.
- Test: `tests/run.sh` per il database, `dotnet test api/Pdmtrack.sln` per l'API (entrambi in GitHub Actions).

## API

API REST in ASP.NET Core (.NET 8) in `api/`: articoli, distinta, where-used, check-out/check-in, revisioni e contenuto dei file. Dettagli e avvio in [`api/README.md`](api/README.md).

## Interfaccia web

Next.js in `web/`, esportata come sito statico e servita dall'API. Dettagli in [`web/README.md`](web/README.md).

## Roadmap

1. **MVP**: vault con versioni, check-in/out, anagrafica articoli, BOM e revisioni.
2. **Fase 2**: add-in SolidWorks (.NET) per sincronizzare proprietà, where-used e BOM automatica (Document Manager API).
3. **Fase 3**: workflow di approvazione, ECO, generazione PDF/STEP al rilascio, viewer 3D.

## Architettura prevista

- Frontend web (React/Next.js)
- Backend: PostgreSQL + storage oggetti S3-compatibile + API REST (Supabase come possibile destinazione futura)
- Worker Windows per lettura dei file SolidWorks
- Add-in SolidWorks in .NET
