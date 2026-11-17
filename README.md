# Pdmtrack

PDM (Product Data Management) per la gestione di file e archivi SolidWorks: articoli, distinte (BOM), where-used, check-in/check-out, versioni, workflow di rilascio e modifiche ECO.

## Stato

Prototipo di interfaccia cliccabile con dati di esempio, senza backend: `prototype/index.html` (si apre direttamente nel browser).

## Roadmap

1. **MVP**: vault con versioni, check-in/out, anagrafica articoli, BOM e revisioni.
2. **Fase 2**: add-in SolidWorks (.NET) per sincronizzare proprietà, where-used e BOM automatica (Document Manager API).
3. **Fase 3**: workflow di approvazione, ECO, generazione PDF/STEP al rilascio, viewer 3D.

## Architettura prevista

- Frontend web (React/Next.js)
- Backend: Postgres + storage oggetti + API REST
- Worker Windows per lettura dei file SolidWorks
- Add-in SolidWorks in .NET
