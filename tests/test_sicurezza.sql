-- Test del passo 1 (sicurezza e integrita'). Da eseguire su un database con migrazioni e seed:
--   tests/run.sh
-- Simula l'API: ruolo pdm_app e utente impostato con pdm.user_login.
\set ON_ERROR_STOP 1
set client_min_messages = warning;

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
-- Un admin per i test di sblocco forzato
insert into app_users (login, name, role) values ('admin', 'Amministratore', 'admin');

set client_min_messages = notice;
set role pdm_app;

-- Identita' ------------------------------------------------------------------
select set_config('pdm.user_login', 'sconosciuto', false);
select t.fails($$select check_out(t.id('120-0002'))$$, 'non registrato', 'utente non registrato rifiutato');
select set_config('pdm.user_login', '', false);
select t.fails($$select check_out(t.id('120-0002'))$$, 'non registrato', 'nessun utente impostato rifiutato');

-- Scritture dirette vietate --------------------------------------------------
select set_config('pdm.user_login', 'mrossi', false);
select t.fails($$update files set locked_by = null$$, 'permission denied', 'non si sblocca un file con update diretto');
select t.fails($$insert into item_events (item_id, text) values (t.id('120-0001'), 'falso')$$, 'permission denied', 'storico non falsificabile');
select t.fails($$update app_users set role = 'admin'$$, 'permission denied', 'un utente non si promuove admin');
select t.fails($$update items set state = 'rel'$$, 'permission denied', 'stato non modificabile con update diretto');
select t.fails($$delete from file_versions$$, 'permission denied', 'versioni non cancellabili');
update items set description = 'Albero condotto rettificato' where code = '120-0002';
select t.ok((select description from items where code = '120-0002') = 'Albero condotto rettificato', 'descrizione modificabile');
select t.fails($$select touch_updated_at()$$, 'permission denied', 'funzioni interne non eseguibili');

-- Lock -----------------------------------------------------------------------
-- 120-0001 e' in check-out ad A. Cavani nel seed
select t.fails($$select check_in(t.id('120-0001'), repeat('a', 64), 100)$$, 'non e'' in check-out a questo utente', 'check-in del file di un altro rifiutato');
select t.fails($$select undo_check_out(t.id('120-0001'))$$, 'non e'' in check-out a questo utente', 'annulla check-out del file di un altro rifiutato');
select t.fails($$select check_out(t.id('120-0001'))$$, 'altro utente', 'check-out di un file bloccato rifiutato');
select t.fails($$select check_out(t.id('111-0001'))$$, 'solo in lavorazione', 'check-out di un file rilasciato rifiutato');
select t.fails($$select force_unlock(t.id('120-0001'), 'serve')$$, 'riservata', 'sblocco forzato vietato ai designer');

-- Atomicita': 120-0001-D e' libero, 111-0002 e' di M. Rossi -> nessuno dei due cambia
select set_config('pdm.user_login', 'acavani', false);
select t.fails($$select check_out_many(array[t.id('120-0001-D'), t.id('111-0002')])$$, 'altro utente', 'check-out multiplo con un file occupato rifiutato');
select t.ok((select locked_by is null from files where item_id = t.id('120-0001-D')), 'check-out multiplo: tutto o niente');
select t.ok(check_out_many(array[t.id('120-0001'), t.id('120-0001-D')]) = 1, 'check-out multiplo: i file gia'' propri non contano');
select undo_check_out(t.id('120-0001-D'));

-- Check-in -------------------------------------------------------------------
select t.fails($$select check_in(t.id('120-0001'), 'non-un-hash', 100)$$, 'sha256_format', 'hash non valido rifiutato');
select t.ok(check_in(t.id('120-0001'), repeat('b', 64), 900000, 'Diametro 20') = 13, 'check-in crea la versione 13');
select t.ok((select locked_by is null and current_version = 13 from files where item_id = t.id('120-0001')), 'check-in rilascia il lock');
select t.ok((select sha256 = repeat('b', 64) and created_by = (current_app_user()).id from file_versions v join files f on f.id = v.file_id
             where f.item_id = t.id('120-0001') and v.version = 13), 'versione con hash e autore corretti');
select check_out(t.id('120-0001'));
select undo_check_out(t.id('120-0001'));
select t.ok((select locked_by is null and current_version = 13 from files where item_id = t.id('120-0001')), 'annulla check-out senza nuova versione');

-- Workflow -------------------------------------------------------------------
select transition_item(t.id('120-0001'), 'review');
select t.fails($$select transition_item(t.id('120-0001'), 'rel')$$, 'riservata', 'un designer non rilascia');
select set_config('pdm.user_login', 'lbianchi', false);
select transition_item(t.id('120-0001'), 'rel');
select t.ok((select state = 'rel' from items where id = t.id('120-0001')), 'un approver rilascia');

-- Sblocco forzato ------------------------------------------------------------
select set_config('pdm.user_login', 'admin', false);
select t.fails($$select force_unlock(t.id('111-0002'), '  ')$$, 'motivo', 'sblocco forzato senza motivo rifiutato');
select force_unlock(t.id('111-0002'), 'M. Rossi in ferie');
select t.ok((select locked_by is null from files where item_id = t.id('111-0002')), 'admin sblocca');
select t.ok(exists (select 1 from item_events where item_id = t.id('111-0002') and text like 'Sblocco forzato (era di M. Rossi)%'), 'sblocco forzato registrato nello storico');

-- Nuovi file -----------------------------------------------------------------
select set_config('pdm.user_login', 'mrossi', false);
select t.ok((select filename from files where item_id = t.id('120-0001')) = '120-0001.SLDPRT', 'nome file del seed come atteso');
select t.fails($$select add_file('120-0003', 'Doppione', 'PRT', '120-0001.sldprt', '\\PDM\', repeat('c', 64), 10)$$,
               'files_filename_unique', 'nome file duplicato (maiuscole diverse) rifiutato');
select t.fails($$select add_file('120-0003', 'Senza estensione', 'PRT', '120-0003.step', '\\PDM\', repeat('c', 64), 10)$$,
               'files_filename_ext', 'solo file SolidWorks');
select t.ok(add_file('120-0003', 'Ingranaggio', 'PRT', '120-0003.SLDPRT', '\\PDM\Pompe\', repeat('d', 64), 2048, '18NiCrMo5', 0.3) is not null, 'add_file crea articolo, file e versione 1');
select t.ok((select v.version = 1 and v.created_by = (current_app_user()).id from file_versions v join files f on f.id = v.file_id
             where f.item_id = t.id('120-0003')), 'versione 1 con autore');

-- Calcoli invariati ----------------------------------------------------------
select t.ok(round(item_mass(t.id('100-0001')), 3) = 8.258, 'massa assieme 8,258 kg');

reset role;
