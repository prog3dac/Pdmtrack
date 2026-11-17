# Interfaccia web

Next.js 16 + React 19 + TypeScript, esportata come sito statico (`output: 'export'`) e servita dall'API ASP.NET Core dalla cartella `wwwroot`. Stessa origine dell'API: il browser fa il login Windows sulle chiamate `/api` senza configurare CORS.

In fase 1 e' in sola lettura: elenco articoli con ricerca e filtri, viste "In check-out" e "In approvazione", scheda con dettagli, distinta esplosa e massa, where-used, versioni, revisioni e storico. Check-out e check-in si fanno dal client Windows.

## Sviluppo

```
npm ci
NEXT_PUBLIC_PDM_DEV_USER=acavani npm run dev    # API in Development su http://localhost:5122 (PDM_API_URL per cambiarla)
```

`NEXT_PUBLIC_PDM_DEV_USER` aggiunge l'intestazione `X-Pdm-User`, accettata dall'API solo in Development.

## Pubblicazione nell'API

```
npm run publish-api    # next build + copia di out/ in api/src/Pdmtrack.Api/wwwroot/
```
