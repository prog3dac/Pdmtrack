# Database

Schema PostgreSQL 16 puro, senza dipendenze da Supabase.

- `migrations/`: da applicare in ordine di nome.
- `seed.sql`: dati di esempio (gli stessi del prototipo). Gli hash dei file sono fittizi.
- Test: `tests/run.sh` crea un database di prova, applica migrazioni e seed ed esegue `tests/test_*.sql`. Gira anche in GitHub Actions.

## Come si collega l'API

1. L'API si collega con un utente membro del ruolo `pdm_app`:
   `create role pdm_api login password '...' in role pdm_app;`
2. Dopo aver autenticato l'utente, apre ogni transazione con
   `select set_config('pdm.user_login', '<login>', true);`
   dove `<login>` corrisponde a `app_users.login`.
3. `pdm_app` legge tutte le tabelle ma scrive solo tramite le funzioni (`add_file`, `check_out`, `check_out_many`, `check_in`, `undo_check_out`, `force_unlock`, `transition_item`, `approve_eco`). Eccezioni: `items.description`, `items.material`, `items.mass_kg` e la tabella `bom_lines`.

I client (web e Windows) non si collegano mai direttamente al database.

## Versioni, revisioni e riferimenti

- **Versione**: ogni check-in crea una riga immutabile in `file_versions` (1, 2, 3... per file), con la revisione dell'articolo in quel momento.
- **Revisione**: una riga in `item_revisions` per ogni lettera (A, B, C...; senza I, O, Q; dopo Z viene AA). Al rilascio registra la versione corrente, chi e quando.
- **Riferimenti**: `file_refs` lega una versione alle versioni esatte dei file che usa (assieme -> componenti, disegno -> modello). Il client li passa a `check_in` e `add_file` come `[{"filename": "111-0001.SLDPRT", "version": 11}]`; senza `version` vale la corrente.
- **Riapertura**: `revision_tree(articolo, 'B')` restituisce tutti i file e le versioni da scaricare per aprire la rev B; senza revisione restituisce la versione corrente.
- **Rilascio**: bloccato se un componente diretto in distinta o il modello di un disegno non sono rilasciati.

## Passaggio a Supabase

Lo schema e' pensato per essere spostato su Supabase senza riscritture:

| Oggi | Su Supabase |
| --- | --- |
| `app_users.login` + `pdm.user_login` | aggiungere `app_users.auth_id uuid references auth.users` e cambiare solo il lookup in `current_app_user()` in `where auth_id = auth.uid()` |
| ruolo `pdm_app` | concedere gli stessi permessi a `authenticated`, togliere tutto ad `anon`, attivare la RLS con policy di sola lettura |
| contenuto dei file su storage S3-compatibile, oggetto = SHA-256 | bucket privato di Supabase Storage (anch'esso S3-compatibile), stessi nomi oggetto |
| `db/migrations/` | copiare in `supabase/migrations/` |
