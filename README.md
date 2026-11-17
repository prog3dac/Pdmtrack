# Pdmtrack

PDM (Product Data Management) per la gestione di file e archivi SolidWorks: articoli, distinte (BOM), where-used, check-in/check-out, versioni, workflow di rilascio e modifiche ECO.

## Stato

Prototipo di interfaccia cliccabile con dati di esempio, senza backend: `prototype/index.html` (si apre direttamente nel browser).

## Database

Schema PostgreSQL (compatibile Supabase) in `supabase/migrations/`, dati di esempio in `supabase/seed.sql`.

- Tabelle: `items`, `files`, `file_versions`, `bom_lines`, `item_events`, `ecos`, `eco_items`, `eco_approvals`, `app_users`.
- Funzioni: `bom_explode` (distinta multilivello), `item_mass`, `check_out`, `check_in`, `transition_item`, `approve_eco`; vista `where_used`.
- RLS attiva, per ora con accesso completo agli utenti autenticati.
- Testato su PostgreSQL 16 locale: migrazione, seed, massa dell'assieme (8,258 kg) e le funzioni di workflow.

## Roadmap

1. **MVP**: vault con versioni, check-in/out, anagrafica articoli, BOM e revisioni.
2. **Fase 2**: add-in SolidWorks (.NET) per sincronizzare proprietà, where-used e BOM automatica (Document Manager API).
3. **Fase 3**: workflow di approvazione, ECO, generazione PDF/STEP al rilascio, viewer 3D.

## Architettura prevista

- Frontend web (React/Next.js)
- Backend: Postgres + storage oggetti + API REST
- Worker Windows per lettura dei file SolidWorks
- Add-in SolidWorks in .NET
