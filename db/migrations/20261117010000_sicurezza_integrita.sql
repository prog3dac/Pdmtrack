-- Pdmtrack, fase 1 passo 1: sicurezza e integrita'
--
-- * L'utente si ricava sempre da current_app_user(), mai da un parametro delle funzioni.
-- * Le scritture su articoli, file, versioni, eventi e utenti passano solo da funzioni
--   security definer; l'API non puo' modificare i lock o falsificare lo storico.
-- * Ogni versione ha contenuto (sha256 + storage_path) obbligatorio.
-- * Nome file unico in tutto il vault (senza distinzione maiuscole/minuscole, come Windows).
--
-- Schema PostgreSQL puro. L'unico punto legato all'autenticazione e' current_app_user():
-- vedi db/README.md per il passaggio a Supabase.

-- Ruoli applicativi ---------------------------------------------------------------

create type user_role as enum ('designer', 'approver', 'admin');
alter table app_users alter column role drop default;
alter table app_users alter column role type user_role using role::user_role;
alter table app_users alter column role set default 'designer';

-- Integrita' dei file -------------------------------------------------------------

alter table file_versions add column sha256 text;
alter table file_versions add constraint file_versions_sha256_format check (sha256 ~ '^[0-9a-f]{64}$');
alter table file_versions alter column sha256 set not null;
alter table file_versions alter column storage_path set not null;
create index file_versions_sha256_idx on file_versions (sha256);

create unique index files_filename_unique on files (lower(filename));
alter table files add constraint files_filename_ext
  check (filename ~* '\.(sldprt|sldasm|slddrw)$');

-- Utente corrente -----------------------------------------------------------------

-- L'API, dopo aver autenticato l'utente, apre ogni transazione con
--   select set_config('pdm.user_login', '<login>', true);
-- I client non si collegano mai direttamente al database: il confine di fiducia e' l'API.
-- Passaggio a Supabase: sostituire il lookup con  where auth_id = auth.uid().
create function current_app_user() returns app_users
language plpgsql stable security definer set search_path = public as $$
declare u app_users;
begin
  select * into u from app_users where login = nullif(current_setting('pdm.user_login', true), '');
  if u.id is null then raise exception 'Utente non registrato in Pdmtrack' using errcode = '42501'; end if;
  return u;
end $$;

create function require_role(variadic p_roles user_role[]) returns app_users
language plpgsql stable security definer set search_path = public as $$
declare u app_users := current_app_user();
begin
  if not (u.role = any (p_roles) or u.role = 'admin') then
    raise exception 'Operazione riservata al ruolo %', array_to_string(p_roles, '/') using errcode = '42501';
  end if;
  return u;
end $$;

-- Le vecchie firme ricevevano l'utente come parametro: vanno eliminate
drop function check_out(uuid, uuid);
drop function check_in(uuid, uuid, text, text, bigint);
drop function transition_item(uuid, text, uuid);
drop function approve_eco(uuid, uuid);

-- Nuovo file nel vault: articolo + file + versione 1 -------------------------------

create function add_file(p_code text, p_description text, p_type item_type, p_filename text,
                         p_folder_path text, p_sha256 text, p_size bigint,
                         p_material text default null, p_mass_kg numeric default 0,
                         p_ref_code text default null, p_comment text default null)
returns uuid language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); it uuid; f uuid; ref uuid;
begin
  if p_ref_code is not null then
    select id into ref from items where code = p_ref_code;
    if ref is null then raise exception 'Articolo di riferimento % inesistente', p_ref_code; end if;
  end if;
  insert into items (code, description, type, material, mass_kg, ref_item_id)
    values (p_code, p_description, p_type, p_material, coalesce(p_mass_kg, 0), ref) returning id into it;
  insert into files (item_id, filename, folder_path, current_version, size_bytes)
    values (it, p_filename, p_folder_path, 1, p_size) returning id into f;
  insert into file_versions (file_id, version, storage_path, sha256, size_bytes, comment, created_by)
    values (f, 1, p_sha256, p_sha256, p_size, coalesce(nullif(p_comment, ''), 'Prima versione'), u.id);
  insert into item_events (item_id, user_id, text) values (it, u.id, 'Creato con ' || p_filename);
  return it;
end $$;

-- Check-out -----------------------------------------------------------------------

-- Blocca uno o piu' file in una sola transazione: o tutti o nessuno
create function check_out_many(p_items uuid[]) returns int
language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); r record; n int := 0;
begin
  for r in select i.id, i.code, i.state, f.id as file_id, f.locked_by
           from items i join files f on f.item_id = i.id
           where i.id = any (p_items) order by i.id      -- ordine fisso: niente deadlock
           for update of i, f loop
    if r.state <> 'wip' then
      raise exception '%: check-out consentito solo in lavorazione (stato %)', r.code, r.state;
    end if;
    if r.locked_by is not null and r.locked_by <> u.id then
      raise exception '%: file gia'' in check-out a un altro utente', r.code;
    end if;
    if r.locked_by is null then
      update files set locked_by = u.id, locked_at = now() where id = r.file_id;
      insert into item_events (item_id, user_id, text) values (r.id, u.id, 'Check-out eseguito');
      n := n + 1;
    end if;
  end loop;
  if (select count(*) from files where item_id = any (p_items)) <> cardinality(array(select distinct unnest(p_items))) then
    raise exception 'Uno o piu'' articoli non hanno un file nel vault';
  end if;
  return n;
end $$;

create function check_out(p_item uuid) returns void
language sql security definer set search_path = public as $$
  select check_out_many(array[p_item]);
$$;

-- Check-in ------------------------------------------------------------------------

-- Il client carica prima il contenuto nello storage (oggetto = sha256), poi chiama check_in
create function check_in(p_item uuid, p_sha256 text, p_size bigint, p_comment text default null)
returns int language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); f files; v int; c text := coalesce(nullif(p_comment, ''), 'Aggiornamento');
begin
  select * into f from files where item_id = p_item for update;
  if f.id is null then raise exception 'Articolo senza file'; end if;
  if f.locked_by is distinct from u.id then raise exception 'Il file non e'' in check-out a questo utente'; end if;
  v := f.current_version + 1;
  insert into file_versions (file_id, version, storage_path, sha256, size_bytes, comment, created_by)
    values (f.id, v, p_sha256, p_sha256, p_size, c, u.id);
  update files set current_version = v, size_bytes = p_size, locked_by = null, locked_at = null where id = f.id;
  insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Check-in v' || v || ': ' || c);
  return v;
end $$;

-- Rilascia il proprio lock senza creare una versione
create function undo_check_out(p_item uuid) returns void
language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); f files;
begin
  select * into f from files where item_id = p_item for update;
  if f.locked_by is distinct from u.id then raise exception 'Il file non e'' in check-out a questo utente'; end if;
  update files set locked_by = null, locked_at = null where id = f.id;
  insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Check-out annullato');
end $$;

-- Sblocco forzato del lock di un altro utente: solo admin, motivo obbligatorio
create function force_unlock(p_item uuid, p_reason text) returns void
language plpgsql security definer set search_path = public as $$
declare u app_users := require_role('admin'); f files; owner text;
begin
  if coalesce(trim(p_reason), '') = '' then raise exception 'Motivo obbligatorio'; end if;
  select * into f from files where item_id = p_item for update;
  if f.locked_by is null then raise exception 'Il file non e'' in check-out'; end if;
  select name into owner from app_users where id = f.locked_by;
  update files set locked_by = null, locked_at = null where id = f.id;
  insert into item_events (item_id, user_id, text)
    values (p_item, u.id, 'Sblocco forzato (era di ' || owner || '): ' || p_reason);
end $$;

-- Workflow di rilascio ------------------------------------------------------------

create function transition_item(p_item uuid, p_to text) returns void
language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); it items; locked uuid;
begin
  select * into it from items where id = p_item for update;
  if it.id is null then raise exception 'Articolo inesistente'; end if;
  select locked_by into locked from files where item_id = p_item;
  if locked is not null and p_to <> 'obs' then raise exception 'Esegui prima il check-in del file'; end if;
  if p_to in ('rel', 'obs') or (p_to = 'wip' and it.state = 'review') then
    perform require_role('approver');
  end if;

  if p_to = 'review' and it.state = 'wip' then
    update items set state = 'review' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Inviato in approvazione (rev ' || it.revision || ')');
  elsif p_to = 'rel' and it.state = 'review' then
    update items set state = 'rel' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Rilasciato in revisione ' || it.revision);
  elsif p_to = 'wip' and it.state in ('review', 'obs') then
    update items set state = 'wip' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Riportato in lavorazione');
  elsif p_to = 'newrev' and it.state = 'rel' then
    update items set state = 'wip', revision = chr(ascii(it.revision) + 1) where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Creata revisione ' || chr(ascii(it.revision) + 1));
  elsif p_to = 'obs' and it.state <> 'obs' then
    update items set state = 'obs' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Reso obsoleto');
  else
    raise exception 'Transizione % non consentita da stato %', p_to, it.state;
  end if;
end $$;

-- Approvazione ECO (interfaccia in fase 3; qui solo messa in sicurezza)
create function approve_eco(p_eco uuid) returns int
language plpgsql security definer set search_path = public as $$
declare u app_users := require_role('approver'); n int := 0; r record; ecode text;
begin
  select code into ecode from ecos where id = p_eco for update;
  if ecode is null then raise exception 'ECO inesistente'; end if;
  update eco_approvals set state = 'ok' where eco_id = p_eco and state = 'wait';
  update ecos set state = 'rel' where id = p_eco;
  for r in select i.id from eco_items ei join items i on i.id = ei.item_id
           where ei.eco_id = p_eco and i.state = 'review' loop
    update items set state = 'rel' where id = r.id;
    insert into item_events (item_id, user_id, text) values (r.id, u.id, 'Rilasciato con ' || ecode);
    n := n + 1;
  end loop;
  return n;
end $$;

-- Permessi ------------------------------------------------------------------------

-- Ruolo applicativo: l'utente con cui si collega l'API ne diventa membro
--   create role pdm_api login password '...' in role pdm_app;
do $$ begin
  if not exists (select 1 from pg_roles where rolname = 'pdm_app') then create role pdm_app nologin; end if;
end $$;
grant usage on schema public to pdm_app;

-- Lettura libera, scrittura solo tramite funzioni
grant select on all tables in schema public to pdm_app;
-- Metadati descrittivi modificabili (stato, revisione e lock no)
grant update (description, material, mass_kg) on items to pdm_app;
-- La distinta resta scrivibile direttamente fino al passo 6 (scritture dal web)
grant insert, update, delete on bom_lines to pdm_app;

-- Funzioni: PostgreSQL concede execute a tutti per default, lo togliamo
revoke execute on all functions in schema public from public;
grant execute on function current_app_user(), add_file(text, text, item_type, text, text, text, bigint, text, numeric, text, text),
  check_out(uuid), check_out_many(uuid[]), check_in(uuid, text, bigint, text), undo_check_out(uuid),
  force_unlock(uuid, text), transition_item(uuid, text), approve_eco(uuid),
  bom_explode(uuid), item_mass(uuid) to pdm_app;
