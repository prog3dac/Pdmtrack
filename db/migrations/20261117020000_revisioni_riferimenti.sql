-- Pdmtrack, fase 1 passo 2: revisioni e riferimenti tra versioni
--
-- * item_revisions: storico delle revisioni; una revisione rilasciata punta alla versione del file.
-- * file_versions.revision: ogni versione sa a quale revisione appartiene.
-- * file_refs: i riferimenti di una versione (assieme -> componenti, disegno -> modello) con la
--   versione esatta usata, per riaprire un assieme com'era a una revisione.
-- * Lettere di revisione senza I, O, Q; dopo Z si passa ad AA.
-- * Rilascio bloccato se componenti o modello di riferimento non sono rilasciati.

-- Lettere di revisione ------------------------------------------------------------

alter table items drop constraint items_revision_check;
alter table items add constraint items_revision_check check (revision ~ '^[A-HJ-NPR-Z]+$');

-- Successiva di una revisione: A..H, J..N, P, R..Z, poi AA, AB, ...
create function next_revision(p_rev text) returns text
language plpgsql immutable set search_path = public as $$
declare
  alphabet constant text := 'ABCDEFGHJKLMNPRSTUVWXYZ';
  chars text[] := regexp_split_to_array(p_rev, '');
  i int := array_length(chars, 1);
  pos int;
begin
  if p_rev !~ '^[A-HJ-NPR-Z]+$' then raise exception 'Revisione non valida: %', p_rev; end if;
  loop
    pos := strpos(alphabet, chars[i]);
    if pos < length(alphabet) then
      chars[i] := substr(alphabet, pos + 1, 1);
      return array_to_string(chars, '');
    end if;
    chars[i] := 'A';
    i := i - 1;
    if i = 0 then return 'A' || array_to_string(chars, ''); end if;
  end loop;
end $$;

-- Storico revisioni ---------------------------------------------------------------

create table item_revisions (
  id              uuid primary key default gen_random_uuid(),
  item_id         uuid not null references items (id) on delete cascade,
  revision        text not null,
  file_version_id uuid references file_versions (id),      -- valorizzato al rilascio
  created_by      uuid references app_users (id),
  created_at      timestamptz not null default now(),
  released_by     uuid references app_users (id),
  released_at     timestamptz,
  obsoleted_at    timestamptz,
  unique (item_id, revision),
  check ((released_at is null) = (file_version_id is null))
);

-- Revisione A alla creazione dell'articolo, nuova riga a ogni cambio di lettera
create function item_revision_row() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if tg_op = 'INSERT' or new.revision is distinct from old.revision then
    insert into item_revisions (item_id, revision, created_by)
      values (new.id, new.revision,
              (select id from app_users where login = nullif(current_setting('pdm.user_login', true), '')))
      on conflict (item_id, revision) do nothing;
  end if;
  return new;
end $$;
create trigger items_revision_row after insert or update of revision on items
  for each row execute function item_revision_row();

-- Versioni legate alla revisione --------------------------------------------------

alter table file_versions add column revision text;
update file_versions v set revision = i.revision
  from files f join items i on i.id = f.item_id where f.id = v.file_id;
alter table file_versions alter column revision set not null;

-- Riferimenti per versione --------------------------------------------------------

create table file_refs (
  parent_version_id uuid not null references file_versions (id) on delete cascade,
  child_file_id     uuid not null,
  child_version     int  not null,
  primary key (parent_version_id, child_file_id),
  foreign key (child_file_id, child_version) references file_versions (file_id, version)
);
create index file_refs_child_idx on file_refs (child_file_id, child_version);

-- Dati esistenti: una riga di revisione per articolo, versioni e riferimenti correnti
insert into item_revisions (item_id, revision, file_version_id, released_at)
  select i.id, i.revision,
         case when i.state in ('rel', 'obs') then v.id end,
         case when i.state in ('rel', 'obs') then now() end
  from items i join files f on f.item_id = i.id
       join file_versions v on v.file_id = f.id and v.version = f.current_version
  on conflict (item_id, revision) do nothing;
update item_revisions r set obsoleted_at = now()
  from items i where i.id = r.item_id and i.state = 'obs' and r.revision = i.revision;

insert into file_refs (parent_version_id, child_file_id, child_version)
  select pv.id, cf.id, cf.current_version
  from bom_lines b
  join files pf on pf.item_id = b.parent_id
  join file_versions pv on pv.file_id = pf.id and pv.version = pf.current_version
  join files cf on cf.item_id = b.child_id
  union
  select pv.id, cf.id, cf.current_version
  from items d
  join files pf on pf.item_id = d.id
  join file_versions pv on pv.file_id = pf.id and pv.version = pf.current_version
  join files cf on cf.item_id = d.ref_item_id
  where d.type = 'DRW' and d.ref_item_id is not null;

-- Scrittura riferimenti -----------------------------------------------------------

-- p_refs: [{"filename": "111-0001.SLDPRT", "version": 11}, ...]; version facoltativa = corrente
create function write_file_refs(p_version_id uuid, p_refs jsonb) returns int
language plpgsql security definer set search_path = public as $$
declare r jsonb; cf files; v int; own uuid; n int := 0;
begin
  if p_refs is null then return 0; end if;
  if jsonb_typeof(p_refs) <> 'array' then raise exception 'I riferimenti devono essere una lista'; end if;
  select file_id into own from file_versions where id = p_version_id;
  for r in select * from jsonb_array_elements(p_refs) loop
    select * into cf from files where lower(filename) = lower(r ->> 'filename');
    if cf.id is null then raise exception 'Riferimento a un file non presente nel vault: %', r ->> 'filename'; end if;
    if cf.id = own then raise exception 'Un file non puo'' riferire se stesso: %', cf.filename; end if;
    v := coalesce((r ->> 'version')::int, cf.current_version);
    if not exists (select 1 from file_versions where file_id = cf.id and version = v) then
      raise exception 'Versione % di % inesistente', v, cf.filename;
    end if;
    insert into file_refs (parent_version_id, child_file_id, child_version) values (p_version_id, cf.id, v)
      on conflict (parent_version_id, child_file_id) do update set child_version = excluded.child_version;
    n := n + 1;
  end loop;
  return n;
end $$;

-- Check-in e nuovo file con revisione e riferimenti -------------------------------

drop function check_in(uuid, text, bigint, text);
create function check_in(p_item uuid, p_sha256 text, p_size bigint, p_comment text default null,
                         p_refs jsonb default null)
returns int language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); f files; it items; v int; vid uuid;
        c text := coalesce(nullif(p_comment, ''), 'Aggiornamento');
begin
  select * into f from files where item_id = p_item for update;
  if f.id is null then raise exception 'Articolo senza file'; end if;
  if f.locked_by is distinct from u.id then raise exception 'Il file non e'' in check-out a questo utente'; end if;
  select * into it from items where id = p_item;
  v := f.current_version + 1;
  insert into file_versions (file_id, version, revision, storage_path, sha256, size_bytes, comment, created_by)
    values (f.id, v, it.revision, p_sha256, p_sha256, p_size, c, u.id) returning id into vid;
  perform write_file_refs(vid, p_refs);
  update files set current_version = v, size_bytes = p_size, locked_by = null, locked_at = null where id = f.id;
  insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Check-in v' || v || ': ' || c);
  return v;
end $$;

drop function add_file(text, text, item_type, text, text, text, bigint, text, numeric, text, text);
create function add_file(p_code text, p_description text, p_type item_type, p_filename text,
                         p_folder_path text, p_sha256 text, p_size bigint,
                         p_material text default null, p_mass_kg numeric default 0,
                         p_ref_code text default null, p_comment text default null,
                         p_refs jsonb default null)
returns uuid language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); it uuid; f uuid; ref uuid; vid uuid;
begin
  if p_ref_code is not null then
    select id into ref from items where code = p_ref_code;
    if ref is null then raise exception 'Articolo di riferimento % inesistente', p_ref_code; end if;
  end if;
  insert into items (code, description, type, material, mass_kg, ref_item_id)
    values (p_code, p_description, p_type, p_material, coalesce(p_mass_kg, 0), ref) returning id into it;
  insert into files (item_id, filename, folder_path, current_version, size_bytes)
    values (it, p_filename, p_folder_path, 1, p_size) returning id into f;
  insert into file_versions (file_id, version, revision, storage_path, sha256, size_bytes, comment, created_by)
    values (f, 1, 'A', p_sha256, p_sha256, p_size, coalesce(nullif(p_comment, ''), 'Prima versione'), u.id)
    returning id into vid;
  perform write_file_refs(vid, p_refs);
  insert into item_events (item_id, user_id, text) values (it, u.id, 'Creato con ' || p_filename);
  return it;
end $$;

-- Workflow con storico revisioni e controlli di rilascio --------------------------

create or replace function transition_item(p_item uuid, p_to text) returns void
language plpgsql security definer set search_path = public as $$
declare u app_users := current_app_user(); it items; locked uuid; cur int; vid uuid; blockers text; nr text;
begin
  select * into it from items where id = p_item for update;
  if it.id is null then raise exception 'Articolo inesistente'; end if;
  select locked_by, current_version into locked, cur from files where item_id = p_item;
  if locked is not null and p_to <> 'obs' then raise exception 'Esegui prima il check-in del file'; end if;
  if p_to in ('rel', 'obs') or (p_to = 'wip' and it.state = 'review') then
    perform require_role('approver');
  end if;

  if p_to = 'review' and it.state = 'wip' then
    update items set state = 'review' where id = p_item;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Inviato in approvazione (rev ' || it.revision || ')');

  elsif p_to = 'rel' and it.state = 'review' then
    -- Componenti diretti e modello di riferimento devono essere gia' rilasciati
    select string_agg(c.code || ' (' || c.state || ')', ', ' order by c.code) into blockers
    from items c
    where c.state <> 'rel'
      and (c.id in (select child_id from bom_lines where parent_id = p_item) or c.id = it.ref_item_id);
    if blockers is not null then
      raise exception 'Rilascio bloccato: non rilasciati %', blockers;
    end if;
    select v.id into vid from files f join file_versions v on v.file_id = f.id and v.version = f.current_version
      where f.item_id = p_item;
    update items set state = 'rel' where id = p_item;
    update item_revisions set file_version_id = vid, released_by = u.id, released_at = now()
      where item_id = p_item and revision = it.revision;
    insert into item_events (item_id, user_id, text)
      values (p_item, u.id, 'Rilasciato in revisione ' || it.revision || ' (v' || cur || ')');

  elsif p_to = 'wip' and it.state in ('review', 'obs') then
    update items set state = 'wip' where id = p_item;
    update item_revisions set obsoleted_at = null where item_id = p_item and revision = it.revision;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Riportato in lavorazione');

  elsif p_to = 'newrev' and it.state = 'rel' then
    nr := next_revision(it.revision);
    update items set state = 'wip', revision = nr where id = p_item;   -- il trigger crea la riga in item_revisions
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Creata revisione ' || nr);

  elsif p_to = 'obs' and it.state <> 'obs' then
    update items set state = 'obs' where id = p_item;
    update item_revisions set obsoleted_at = now() where item_id = p_item and revision = it.revision;
    insert into item_events (item_id, user_id, text) values (p_item, u.id, 'Reso obsoleto');

  else
    raise exception 'Transizione % non consentita da stato %', p_to, it.state;
  end if;
end $$;

-- Lettura: albero di versioni da scaricare ---------------------------------------

-- Tutti i file necessari ad aprire una versione, con la versione esatta di ciascuno
create function version_tree(p_version_id uuid)
returns table (level int, file_id uuid, filename text, folder_path text, version int,
               sha256 text, storage_path text, size_bytes bigint)
language sql stable security definer set search_path = public as $$
  with recursive t as (
    select 0 as level, v.id as version_id, array[v.file_id] as chain
    from file_versions v where v.id = p_version_id
    union all
    select t.level + 1, cv.id, t.chain || cv.file_id
    from t join file_refs r on r.parent_version_id = t.version_id
           join file_versions cv on cv.file_id = r.child_file_id and cv.version = r.child_version
    where cv.file_id <> all (t.chain)
  )
  select distinct on (f.id) t.level, f.id, f.filename, f.folder_path, v.version, v.sha256, v.storage_path, v.size_bytes
  from t join file_versions v on v.id = t.version_id join files f on f.id = v.file_id
  order by f.id, t.level;
$$;

-- Comodita': albero della revisione di un articolo (rilasciata o, se in lavorazione, versione corrente)
create function revision_tree(p_item uuid, p_revision text default null)
returns table (level int, file_id uuid, filename text, folder_path text, version int,
               sha256 text, storage_path text, size_bytes bigint)
language plpgsql stable security definer set search_path = public as $$
declare it items; vid uuid;
begin
  select * into it from items where id = p_item;
  if it.id is null then raise exception 'Articolo inesistente'; end if;
  select r.file_version_id into vid from item_revisions r
    where r.item_id = p_item and r.revision = coalesce(p_revision, it.revision);
  if vid is null then
    if p_revision is not null and p_revision <> it.revision then
      raise exception 'Revisione % di % non rilasciata', p_revision, it.code;
    end if;
    select v.id into vid from files f join file_versions v on v.file_id = f.id and v.version = f.current_version
      where f.item_id = p_item;
  end if;
  return query select * from version_tree(vid) order by 1, 3;
end $$;

-- Albero "ultima versione": parte dalla versione corrente e segue i riferimenti verso i file,
-- prendendo di ognuno la versione corrente (e i suoi riferimenti correnti). E' il "get latest"
-- della cartella di lavoro; revision_tree invece riproduce le versioni esatte registrate.
create function latest_tree(p_item uuid)
returns table (level int, file_id uuid, filename text, folder_path text, version int,
               sha256 text, storage_path text, size_bytes bigint)
language sql stable security definer set search_path = public as $$
  with recursive t as (
    select 0 as level, f.id as file_id, array[f.id] as chain
    from files f where f.item_id = p_item
    union all
    select t.level + 1, r.child_file_id, t.chain || r.child_file_id
    from t join files pf on pf.id = t.file_id
           join file_versions pv on pv.file_id = pf.id and pv.version = pf.current_version
           join file_refs r on r.parent_version_id = pv.id
    where r.child_file_id <> all (t.chain)
  )
  select distinct on (f.id) t.level, f.id, f.filename, f.folder_path, v.version, v.sha256, v.storage_path, v.size_bytes
  from t join files f on f.id = t.file_id
         join file_versions v on v.file_id = f.id and v.version = f.current_version
  order by f.id, t.level;
$$;

-- Permessi ------------------------------------------------------------------------

grant select on item_revisions, file_refs to pdm_app;
revoke execute on function next_revision(text), item_revision_row(), write_file_refs(uuid, jsonb),
  check_in(uuid, text, bigint, text, jsonb),
  add_file(text, text, item_type, text, text, text, bigint, text, numeric, text, text, jsonb),
  version_tree(uuid), revision_tree(uuid, text), latest_tree(uuid) from public;
grant execute on function next_revision(text),
  check_in(uuid, text, bigint, text, jsonb),
  add_file(text, text, item_type, text, text, text, bigint, text, numeric, text, text, jsonb),
  version_tree(uuid), revision_tree(uuid, text), latest_tree(uuid) to pdm_app;
