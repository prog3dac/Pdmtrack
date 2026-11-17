-- Funzioni di supporto ai test (schema t), incluse da ogni tests/test_*.sql
create schema t;
create function t.ok(cond boolean, what text) returns void language plpgsql as $$
begin
  if not coalesce(cond, false) then raise exception 'FALLITO: %', what; end if;
  raise notice 'ok - %', what;
end $$;
-- Esegue sql e verifica che fallisca con un messaggio che contiene pattern
create function t.fails(sql text, pattern text, what text) returns void language plpgsql as $$
begin
  begin
    execute sql;
  exception when others then
    if sqlerrm ilike '%' || pattern || '%' then raise notice 'ok - %', what; return; end if;
    raise exception 'FALLITO: % (errore inatteso: %)', what, sqlerrm;
  end;
  raise exception 'FALLITO: % (nessun errore)', what;
end $$;
create function t.id(p_code text) returns uuid language sql stable as $$ select id from public.items where code = p_code $$;
grant usage on schema t to pdm_app;
grant execute on all functions in schema t to pdm_app;
