// Le classi di test creano database e ruoli PostgreSQL (pdm_app, pdm_api) a livello di cluster:
// in parallelo, su un cluster nuovo, due "create role" concorrenti vanno in conflitto.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
