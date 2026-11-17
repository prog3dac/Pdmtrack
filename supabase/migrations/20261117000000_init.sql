-- Pdmtrack: schema iniziale (articoli, file, versioni, distinta, ECO)

create type item_type  as enum ('PRT', 'ASM', 'DRW');
create type item_state as enum ('wip', 'review', 'rel', 'obs');
create type eco_state  as enum ('review', 'rel', 'obs');
create type approval_state as enum ('wait', 'ok', 'rejected');

-- Utenti applicativi (collegabili a Supabase Auth)
create table app_users (
  id         uuid primary key default gen_random_uuid(),
  auth_id    uuid unique references auth.users (id) on delete set null,
  name       text not null,
  role       text not null default 'designer',
  created_at timestamptz not null default now()
);

-- Anagrafica articoli (una riga per codice)
create table items (
  id          uuid primary key default gen_random_uuid(),
  code        text not null unique,
  description text not null,
  type        item_type  not null,
  revision    text       not null default 'A' check (revision ~ '^[A-Z]$'),
  state       item_state not null default 'wip',
  material    text,
  mass_kg     numeric(12,4) not null default 0 check (mass_kg >= 0), -- solo parti: gli assiemi sono calcolati
  ref_item_id uuid references items (id),                              -- disegno -> modello di riferimento
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now()
);

-- File SolidWorks collegato all'articolo, con blocco di check-out
create table files (
  id              uuid primary key default gen_random_uuid(),
  item_id         uuid not null unique references items (id) on delete cascade,
  filename        text not null,
  folder_path     text not null,
  current_version int  not null default 1,
  size_bytes      bigint not null default 0,
  locked_by       uuid references app_users (id),
  locked_at       timestamptz,
  check (locked_by is not null or locked_at is null)
);

-- Ogni versione e' immutabile; il contenuto vive nello storage oggetti
create table file_versions (
  id           uuid primary key default gen_random_uuid(),
  file_id      uuid not null references files (id) on delete cascade,
  version      int  not null,
  storage_path text,
  size_bytes   bigint not null default 0,
  comment      text,
  created_by   uuid references app_users (id),
  created_at   timestamptz not null default now(),
  unique (file_id, version)
);

-- Distinta: riferimenti assieme -> componente
create table bom_lines (
  parent_id uuid not null references items (id) on delete cascade,
  child_id  uuid not null references items (id),
  qty       numeric(10,3) not null check (qty > 0),
  primary key (parent_id, child_id),
  check (parent_id <> child_id)
);
create index bom_lines_child_idx on bom_lines (child_id);

-- Storico eventi per articolo
create table item_events (
  id         bigint generated always as identity primary key,
  item_id    uuid not null references items (id) on delete cascade,
  user_id    uuid references app_users (id),
  text       text not null,
  created_at timestamptz not null default now()
);
create index item_events_item_idx on item_events (item_id, created_at desc);

-- Modifiche tecniche
create table ecos (
  id           uuid primary key default gen_random_uuid(),
  code         text not null unique,
  title        text not null,
  state        eco_state not null default 'review',
  requested_by uuid references app_users (id),
  created_at   timestamptz not null default now()
);
create table eco_items (
  eco_id  uuid not null references ecos (id) on delete cascade,
  item_id uuid not null references items (id),
  primary key (eco_id, item_id)
);
create table eco_approvals (
  eco_id  uuid not null references ecos (id) on delete cascade,
  user_id uuid not null references app_users (id),
  state   approval_state not null default 'wait',
  primary key (eco_id, user_id)
);

-- Utilita'
create function touch_updated_at() returns trigger language plpgsql as $$
begin new.updated_at = now(); return new; end $$;
create trigger items_touch before update on items
  for each row execute function touch_updated_at();

-- Where-used: assiemi che usano un articolo
create view where_used as
  select b.child_id as item_id, p.id as parent_id, p.code as parent_code,
         p.description as parent_description, p.state as parent_state, b.qty
  from bom_lines b join items p on p.id = b.parent_id;

-- Distinta esplosa a piu' livelli, con quantita' cumulate
create function bom_explode(root uuid)
returns table (level int, path text, item_id uuid, code text, description text,
               qty numeric, total_qty numeric, state item_state, unit_mass_kg numeric)
language sql stable as $$
  with recursive t as (
    select 1 as level, i.code::text as path, i.id as item_id, b.qty::numeric as qty, b.qty::numeric as total_qty,
           array[root, i.id] as chain
    from bom_lines b join items i on i.id = b.child_id
    where b.parent_id = root
    union all
    select t.level + 1, t.path || '/' || i.code, i.id, b.qty, t.total_qty * b.qty,
           t.chain || i.id
    from t join bom_lines b on b.parent_id = t.item_id
           join items i on i.id = b.child_id
    where i.id <> all (t.chain)           -- difesa contro riferimenti circolari
  )
  select t.level, t.path, i.id, i.code, i.description, t.qty, t.total_qty, i.state, i.mass_kg
  from t join items i on i.id = t.item_id
  order by t.path;
$$;

-- Massa totale: somma delle parti foglia con quantita' cumulate
create function item_mass(root uuid) returns numeric language sql stable as $$
  select case when exists (select 1 from bom_lines where parent_id = root)
    then coalesce((select sum(e.total_qty * e.unit_mass_kg) from bom_explode(root) e
                   where not exists (select 1 from bom_lines where parent_id = e.item_id)), 0)
    else (select mass_kg from items where id = root) end;
$$;

-- Check-out: blocca il file per l'utente (solo se non rilasciato/obsoleto e libero)
create function check_out(p_item uuid, p_user uuid) returns void
language plpgsql as $$
declare it items; f files;
begin
  select * into it from items where id = p_item for update;
  if it.state in ('rel','obs') then raise exception 'File rilasciato o obsoleto: crea una nuova revisione'; end if;
  select * into f from files where item_id = p_item for update;
  if f.locked_by is not null then raise exception 'File gia'' in check-out'; end if;
  update files set locked_by = p_user, locked_at = now() where id = f.id;
  insert into item_events (item_id, user_id, text) values (p_item, p_user, 'Check-out eseguito');
end $$;

-- Check-in: nuova versione immutabile e rilascio del blocco
create function check_in(p_item uuid, p_user uuid, p_comment text, p_storage_path text default null, p_size bigint default 0)
returns int language plpgsql as $$
declare f files; v int;
begin
  select * into f from files where item_id = p_item for update;
  if f.locked_by is distinct from p_user then raise exception 'Il file non e'' in check-out a questo utente'; end if;
  v := f.current_version + 1;
  insert into file_versions (file_id, version, storage_path, size_bytes, comment, created_by)
    values (f.id, v, p_storage_path, p_size, coalesce(nullif(p_comment, ''), 'Aggiornamento'), p_user);
  update files set current_version = v, size_bytes = greatest(p_size, size_bytes),
                   locked_by = null, locked_at = null where id = f.id;
  insert into item_events (item_id, user_id, text)
    values (p_item, p_user, 'Check-in v' || v || ': ' || coalesce(nullif(p_comment, ''), 'Aggiornamento'));
  return v;
end $$;

-- Cambi di stato del workflow di rilascio
create function transition_item(p_item uuid, p_to text, p_user uuid) returns void
language plpgsql as $$
declare it items; locked uuid;
begin
  select * into it from items where id = p_item for update;
  select locked_by into locked from files where item_id = p_item;
  if locked is not null and p_to <> 'obs' then raise exception 'Esegui prima il check-in del file'; end if;
  if p_to = 'review' and it.state = 'wip' then
    update items set state = 'review' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, p_user, 'Inviato in approvazione (rev ' || it.revision || ')');
  elsif p_to = 'rel' and it.state = 'review' then
    update items set state = 'rel' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, p_user, 'Rilasciato in revisione ' || it.revision);
  elsif p_to = 'wip' and it.state in ('review','obs') then
    update items set state = 'wip' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, p_user, 'Riportato in lavorazione');
  elsif p_to = 'newrev' and it.state = 'rel' then
    update items set state = 'wip', revision = chr(ascii(it.revision) + 1) where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, p_user, 'Creata revisione ' || chr(ascii(it.revision) + 1));
  elsif p_to = 'obs' and it.state <> 'obs' then
    update items set state = 'obs' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, p_user, 'Reso obsoleto');
  else
    raise exception 'Transizione % non consentita da stato %', p_to, it.state;
  end if;
end $$;

-- Approvazione ECO: rilascia gli articoli collegati che sono in approvazione
create function approve_eco(p_eco uuid, p_user uuid) returns int
language plpgsql as $$
declare n int := 0; r record; ecode text;
begin
  select code into ecode from ecos where id = p_eco for update;
  update eco_approvals set state = 'ok' where eco_id = p_eco and state = 'wait';
  update ecos set state = 'rel' where id = p_eco;
  for r in select i.id from eco_items ei join items i on i.id = ei.item_id
           where ei.eco_id = p_eco and i.state = 'review' loop
    update items set state = 'rel' where id = r.id;
    insert into item_events (item_id, user_id, text) values (r.id, p_user, 'Rilasciato con ' || ecode);
    n := n + 1;
  end loop;
  return n;
end $$;

-- Sicurezza: RLS attiva; per ora accesso completo agli utenti autenticati
do $$
declare t text;
begin
  foreach t in array array['app_users','items','files','file_versions','bom_lines','item_events','ecos','eco_items','eco_approvals'] loop
    execute format('alter table %I enable row level security', t);
    execute format('create policy "authenticated full access" on %I for all to authenticated using (true) with check (true)', t);
  end loop;
end $$;
