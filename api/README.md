# API REST

ASP.NET Core (.NET 8), minimal API. Si collega a PostgreSQL con un utente membro di `pdm_app` e, per ogni richiesta, apre una transazione impostando `pdm.user_login` con l'utente autenticato: tutti i controlli (lock, ruoli, workflow) restano nelle funzioni SQL.

## Avvio in sviluppo

```
createdb pdmtrack && for f in db/migrations/*.sql db/seed.sql; do psql -d pdmtrack -f "$f"; done
psql -d pdmtrack -c "create role pdm_api login password 'pdm_api' in role pdm_app"
cd api/src/Pdmtrack.Api
ConnectionStrings__Pdm="Host=localhost;Database=pdmtrack;Username=pdm_api;Password=pdm_api" dotnet run
curl -H "X-Pdm-User: acavani" http://localhost:5122/api/items?q=albero
```

In Development l'utente si passa nell'intestazione `X-Pdm-User`. Altrove l'autenticazione e' Windows (Kerberos/NTLM): `DOMINIO\acavani` diventa il login `acavani` di `app_users`.

## Configurazione

| Chiave | Valori |
| --- | --- |
| `ConnectionStrings:Pdm` | stringa di connessione Npgsql dell'utente `pdm_api` |
| `Pdm:Auth:Mode` | `Windows` (predefinito) o `Dev` (solo Development/Testing) |
| `Pdm:Storage:Kind` | `FileSystem` (cartella `Pdm:Storage:Path`) o `S3` (`Pdm:Storage:S3:ServiceUrl`, `Bucket`, `AccessKey`, `SecretKey`; MinIO, Supabase Storage o AWS) |

## Endpoint

Tutti sotto `/api`, tutti richiedono login. Errori come `application/problem+json` con il messaggio in italiano delle funzioni SQL (`403` permessi, `409` conflitti di stato o lock, `400` dati non validi, `404` articolo inesistente).

| Metodo e percorso | Cosa fa |
| --- | --- |
| `GET /me` | utente corrente e ruolo |
| `GET /items?q=&type=&state=&limit=` | ricerca per codice o descrizione |
| `GET /items/{code}` | scheda: articolo, file, lock, revisioni, versioni, storico |
| `PATCH /items/{code}` | descrizione, materiale, massa |
| `GET /items/{code}/bom` | distinta esplosa e massa totale |
| `GET /items/{code}/where-used` | assiemi che usano l'articolo |
| `GET /items/{code}/tree?revision=` | file e versioni esatte da scaricare per aprire la revisione |
| `PUT /blobs/{sha256}` | carica il contenuto; il server ricalcola l'hash |
| `HEAD`/`GET /blobs/{sha256}` | verifica o scarica il contenuto |
| `POST /items` | nuovo file nel vault (`add_file`); il contenuto deve essere gia' caricato |
| `POST /checkout` | `{"codes": [...]}`, tutto o niente |
| `POST /items/{code}/checkin` | `{"sha256", "size", "comment", "refs": [{"filename", "version"}]}` |
| `POST /items/{code}/undo-checkout` | rilascia il proprio lock senza nuova versione |
| `POST /items/{code}/force-unlock` | `{"reason"}`, solo admin |
| `POST /items/{code}/transition` | `{"to": "review" \| "rel" \| "wip" \| "newrev" \| "obs"}` |

## Test

`dotnet test api/Pdmtrack.sln` crea un database temporaneo da `db/`, avvia l'API collegata come `pdm_api` e prova gli endpoint. Connessione amministrativa da `PDM_TEST_ADMIN` (predefinita `Host=localhost;Username=postgres;Password=postgres`).
