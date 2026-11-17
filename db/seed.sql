-- Dati di esempio (stessi del prototipo)
insert into app_users (login, name, role) values
  ('acavani','A. Cavani','designer'), ('mrossi','M. Rossi','designer'), ('lbianchi','L. Bianchi','approver');

create temp table s (code text, descr text, type item_type, rev text, state item_state, mat text, mass numeric,
                     folder text, ver int, size_mb numeric, locked text, ref text);
insert into s values
 ('100-0001','Gruppo pompa ad ingranaggi PG-25','ASM','C','review',null,0,'Pompe\PG-25\',14,2.4,null,null),
 ('100-0001-D','Disegno gruppo pompa PG-25','DRW','C','review',null,0,'Pompe\PG-25\Disegni\',9,0.9,null,'100-0001'),
 ('110-0001','Corpo pompa completo','ASM','B','rel',null,0,'Pompe\PG-25\Corpo\',8,1.7,null,null),
 ('111-0001','Corpo pompa','PRT','B','rel','Ghisa EN-GJL-250',4.82,'Pompe\PG-25\Corpo\',11,3.1,null,null),
 ('111-0001-D','Disegno corpo pompa','DRW','B','rel',null,0,'Pompe\PG-25\Corpo\Disegni\',7,0.6,null,'111-0001'),
 ('111-0002','Coperchio pompa','PRT','B','wip','Ghisa EN-GJL-250',1.36,'Pompe\PG-25\Corpo\',6,1.4,'M. Rossi',null),
 ('120-0001','Albero conduttore','PRT','C','wip','42CrMo4 bonificato',0.74,'Pompe\PG-25\Alberi\',12,0.8,'A. Cavani',null),
 ('120-0001-D','Disegno albero conduttore','DRW','C','wip',null,0,'Pompe\PG-25\Alberi\Disegni\',6,0.4,null,'120-0001'),
 ('120-0002','Albero condotto','PRT','A','rel','42CrMo4 bonificato',0.61,'Pompe\PG-25\Alberi\',4,0.7,null,null),
 ('130-0001','Gruppo tenuta albero','ASM','A','rel',null,0,'Pompe\PG-25\Tenuta\',3,0.5,null,null),
 ('131-0001','Anello di tenuta radiale 18x30x7','PRT','A','rel','NBR 70 Shore',0.02,'Pompe\PG-25\Tenuta\',2,0.2,null,null),
 ('131-0002','Flangia di tenuta','PRT','A','rel','AISI 304',0.38,'Pompe\PG-25\Tenuta\',3,0.5,null,null),
 ('150-0001','Spina di centraggio Ø8x20','PRT','A','rel','C45 rettificato',0.008,'Pompe\PG-25\Normalizzati\',1,0.1,null,null),
 ('150-0002','Vite TCEI M8x25 UNI 5931','PRT','A','rel','Acciaio 8.8 zincato',0.021,'Pompe\PG-25\Normalizzati\',1,0.1,null,null),
 ('150-0003','Rondella Grower M8','PRT','A','rel','Acciaio armonico',0.003,'Pompe\PG-25\Normalizzati\',1,0.1,null,null),
 ('190-0001','Kit viteria coperchio','ASM','A','rel',null,0,'Pompe\PG-25\Normalizzati\',2,0.3,null,null),
 ('111-0003','Tappo di scarico G1/4 (sostituito)','PRT','A','obs','Acciaio zincato',0.045,'Pompe\PG-25\Corpo\Archivio\',2,0.2,null,null);

insert into items (code, description, type, revision, state, material, mass_kg)
  select code, descr, type, rev, state, mat, mass from s;
update items i set ref_item_id = r.id from s join items r on r.code = s.ref where s.code = i.code and s.ref is not null;

insert into files (item_id, filename, folder_path, current_version, size_bytes, locked_by, locked_at)
  select i.id,
         s.code || case s.type when 'PRT' then '.SLDPRT' when 'ASM' then '.SLDASM' else '.SLDDRW' end,
         '\\PDM\' || s.folder, s.ver, (s.size_mb * 1048576)::bigint, u.id,
         case when u.id is not null then now() end
  from s join items i on i.code = s.code left join app_users u on u.name = s.locked;

-- Hash fittizi ma ben formati: nello storage di sviluppo gli oggetti non esistono.
-- Ogni tre versioni all'indietro si scende di una revisione (v11-v9 = B, v8-v7 = A).
insert into file_versions (file_id, version, revision, storage_path, sha256, size_bytes, comment, created_by)
  select f.id, g, chr(greatest(ascii('A'), ascii(i.revision) - (f.current_version - g) / 3)),
         h.sha, h.sha, f.size_bytes, case when g = 1 then 'Prima versione' else 'Aggiornamento' end,
         (select id from app_users where name = 'M. Rossi')
  from files f join items i on i.id = f.item_id,
       lateral generate_series(greatest(1, f.current_version - 4), f.current_version) g,
       lateral (select encode(sha256(convert_to(f.filename || ' v' || g, 'UTF8')), 'hex') as sha) h;

-- Revisioni precedenti, rilasciate all'ultima loro versione (la corrente la crea il trigger)
insert into item_revisions (item_id, revision, file_version_id, released_at)
  select f.item_id, v.revision, (array_agg(v.id order by v.version desc))[1], now() - interval '30 days'
  from file_versions v join files f on f.id = v.file_id join items i on i.id = f.item_id
  where v.revision <> i.revision
  group by f.item_id, v.revision;
-- Revisione corrente degli articoli rilasciati o obsoleti
update item_revisions r set file_version_id = v.id, released_at = now() - interval '7 days',
       obsoleted_at = case when i.state = 'obs' then now() end
  from items i join files f on f.item_id = i.id
       join file_versions v on v.file_id = f.id and v.version = f.current_version
  where r.item_id = i.id and r.revision = i.revision and i.state in ('rel', 'obs');

create temp table b (parent text, child text, qty numeric);
insert into b values
 ('100-0001','110-0001',1),('100-0001','120-0001',1),('100-0001','120-0002',1),('100-0001','130-0001',1),('100-0001','190-0001',1),
 ('110-0001','111-0001',1),('110-0001','111-0002',1),('110-0001','150-0001',2),('110-0001','150-0002',8),
 ('130-0001','131-0001',1),('130-0001','131-0002',1),
 ('190-0001','150-0002',6),('190-0001','150-0003',6);
insert into bom_lines (parent_id, child_id, qty)
  select p.id, c.id, b.qty from b join items p on p.code = b.parent join items c on c.code = b.child;

-- Riferimenti delle versioni correnti: assiemi -> componenti, disegni -> modello
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
  where d.type = 'DRW';

insert into ecos (code, title, state, requested_by, created_at) values
 ('ECO-0042','Albero conduttore da Ø18 a Ø20 per maggiore rigidità','review',(select id from app_users where name='A. Cavani'),'2026-11-12'),
 ('ECO-0041','Nuovo trattamento superficiale coperchio pompa','rel',(select id from app_users where name='M. Rossi'),'2026-10-28'),
 ('ECO-0040','Sostituzione tappo di scarico con versione G1/4 inox','obs',(select id from app_users where name='L. Bianchi'),'2026-02-09');
insert into eco_items select e.id, i.id from (values ('ECO-0042','120-0001'),('ECO-0042','100-0001'),('ECO-0041','111-0002'),('ECO-0040','111-0003')) v(e,c)
  join ecos e on e.code = v.e join items i on i.code = v.c;
insert into eco_approvals select e.id, u.id, v.s::approval_state from (values
  ('ECO-0042','M. Rossi','ok'),('ECO-0042','L. Bianchi','wait'),
  ('ECO-0041','A. Cavani','ok'),('ECO-0041','L. Bianchi','ok'),
  ('ECO-0040','M. Rossi','ok'),('ECO-0040','A. Cavani','ok')) v(e,n,s)
  join ecos e on e.code = v.e join app_users u on u.name = v.n;
