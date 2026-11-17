# Client Windows `pdm`

Client da riga di comando (.NET 8) per lavorare con il vault da una cartella locale. In fase 2 lo stesso codice diventa la base dell'add-in SolidWorks.

## Come funziona

- La cartella di lavoro (predefinita `C:\PDM`) rispecchia il vault: `\\PDM\Pompe\PG-25\111-0001.SLDPRT` diventa `C:\PDM\Pompe\PG-25\111-0001.SLDPRT`. Cosi' SolidWorks trova i componenti degli assiemi.
- Senza check-out i file sono in sola lettura: SolidWorks li apre senza poterli salvare.
- Un file modificato in locale non viene mai sovrascritto senza `--force`. Il client ricorda cosa ha scaricato in `C:\PDM\.pdmtrack\state.json`.
- Ogni download e' verificato con lo SHA-256; ogni caricamento viene ricontrollato dal server.
- Login: account Windows di dominio (Kerberos/NTLM), nessuna password da inserire.

## Comandi

```
pdm config --server http://pdm-server:5000 --root C:\PDM
pdm whoami
pdm status                         file in check-out a te, file modificati, file non nel vault
pdm get 100-0001                   assieme + tutti i componenti, ultima versione
pdm get 111-0001 --rev A           i file esattamente come erano alla revisione A
pdm checkout 111-0002 120-0001     blocca (tutti o nessuno) e rende scrivibili
pdm checkin 111-0002 -m "Nuovo trattamento"
pdm undo 111-0002                  annulla il check-out e ripristina il file del vault
pdm add C:\PDM\Pompe\PG-25\160-0001.SLDPRT --code 160-0001 --desc "Perno" --material C45 --mass 0,12
```

Riferimenti di assiemi e disegni: in fase 1 il client non legge i file SolidWorks. Al check-in di un assieme o disegno riusa i riferimenti della versione precedente, aggiornati all'ultima versione di ogni componente; per cambiarli usa `--refs 111-0001.SLDPRT,150-0002.SLDPRT`. In fase 2 l'add-in li leggera' dal file.

## Compilare `pdm.exe`

```
dotnet publish client/src/Pdmtrack.Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```

Produce un unico `pdm.exe` (circa 65 MB) che non richiede .NET installato. La CI lo compila a ogni push: si scarica come artefatto `pdm-win-x64` dalla pagina dell'esecuzione in GitHub Actions.

## Prova su Windows con SolidWorks

1. Server: PostgreSQL 16 con `db/migrations` e `db/seed.sql`, ruolo `pdm_api` (vedi `api/README.md`), API avviata. Per una prova su una sola macchina basta `dotnet run` in Development e `pdm config --dev-user acavani`.
2. Il seed non ha i file veri nello storage: per provare con SolidWorks aggiungi file reali con `pdm add` (prima le parti, poi l'assieme con `--refs`, poi il disegno con `--ref CODICE_MODELLO`).
3. Da un secondo utente o cartella (`pdm config --root C:\PDM2`): `pdm get` dell'assieme, aprilo in SolidWorks e verifica che trovi tutti i componenti e che siano in sola lettura.
4. `pdm checkout` di una parte, modifica e salva in SolidWorks, chiudi il file, `pdm checkin`. Dal primo utente `pdm get` dell'assieme: la parte si aggiorna.

## Test

I test automatici del client sono in `api/tests/Pdmtrack.Api.Tests/ClientTests.cs`: due utenti, due cartelle di lavoro, ciclo completo contro l'API reale.
