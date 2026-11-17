#!/usr/bin/env bash
# Crea un database di prova, applica migrazioni e seed, esegue i test.
# Uso: tests/run.sh   (variabili PG* standard per la connessione; serve un utente che possa creare database e ruoli)
set -euo pipefail
cd "$(dirname "$0")/.."
DB="${TEST_DB:-pdmtrack_test}"
dropdb --if-exists "$DB"
createdb "$DB"
for f in db/migrations/*.sql db/seed.sql; do
  psql -q -v ON_ERROR_STOP=1 -d "$DB" -f "$f"
done
for t in tests/test_*.sql; do
  echo "== $t"
  psql -q -v ON_ERROR_STOP=1 -d "$DB" -f "$t" 2>&1 | sed 's/.*NOTICE:  //'
  test "${PIPESTATUS[0]}" -eq 0
done
echo "Tutti i test superati."
