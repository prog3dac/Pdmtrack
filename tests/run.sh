#!/usr/bin/env bash
# Per ogni tests/test_*.sql crea un database di prova, applica migrazioni e seed ed esegue il test.
# Uso: tests/run.sh   (variabili PG* standard per la connessione; serve un utente che possa creare database e ruoli)
set -euo pipefail
cd "$(dirname "$0")/.."
DB="${TEST_DB:-pdmtrack_test}"
for t in tests/test_*.sql; do
  echo "== $t"
  dropdb --if-exists "$DB" 2>/dev/null
  createdb "$DB"
  for f in db/migrations/*.sql db/seed.sql; do
    psql -q -o /dev/null -v ON_ERROR_STOP=1 -d "$DB" -f "$f"
  done
  psql -q -o /dev/null -v ON_ERROR_STOP=1 -d "$DB" -f "$t" 2>&1 | sed 's/.*NOTICE:  //'
done
echo "Tutti i test superati."
