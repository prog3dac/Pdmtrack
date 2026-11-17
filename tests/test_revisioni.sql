-- Test del passo 2 (revisioni e riferimenti). Eseguire con tests/run.sh
\set ON_ERROR_STOP 1
set client_min_messages = warning;
\ir helpers.sql
create function t.vid(p_code text, p_version int) returns uuid language sql stable as $$
  select v.id from public.file_versions v join public.files f on f.id = v.file_id join public.items i on i.id = f.item_id
  where i.code = p_code and v.version = p_version $$;
grant execute on function t.vid(text, int) to pdm_app;
set client_min_messages = notice;
set role pdm_app;

-- Lettere di revisione -------------------------------------------------------
select t.ok(next_revision('A') = 'B' and next_revision('H') = 'J' and next_revision('N') = 'P'
            and next_revision('P') = 'R', 'si saltano I, O, Q');
select t.ok(next_revision('Z') = 'AA' and next_revision('AZ') = 'BA' and next_revision('ZZ') = 'AAA', 'dopo Z si passa ad AA');
select t.fails($$select next_revision('I')$$, 'non valida', 'lettera I rifiutata');

-- Storico dal seed -----------------------------------------------------------
select t.ok((select string_agg(r.revision || '=v' || v.version, ',' order by r.revision)
             from item_revisions r join file_versions v on v.id = r.file_version_id
             where r.item_id = t.id('111-0001')) = 'A=v8,B=v11', 'corpo pompa: rev A alla v8, rev B alla v11');
select t.ok((select string_agg(revision, '' order by version) from file_versions v join files f on f.id = v.file_id
             where f.item_id = t.id('111-0001')) = 'AABBB', 'ogni versione sa la sua revisione');
select t.ok((select count(*) from revision_tree(t.id('110-0001'))) = 5, 'albero assieme corpo pompa: 5 file');
select t.ok((select string_agg(filename || ' v' || version, ', ' order by filename) from revision_tree(t.id('110-0001')))
            = '110-0001.SLDASM v8, 111-0001.SLDPRT v11, 111-0002.SLDPRT v6, 150-0001.SLDPRT v1, 150-0002.SLDPRT v1',
            'albero assieme con le versioni esatte');
select t.ok((select count(*) from revision_tree(t.id('100-0001'))) = 13, 'albero gruppo pompa: 13 file su 3 livelli');
select t.ok((select string_agg(filename || ' v' || version, ', ') from revision_tree(t.id('111-0001'), 'A')) = '111-0001.SLDPRT v8',
            'rev A del corpo pompa riapribile alla v8');
select t.fails($$select * from revision_tree(t.id('111-0001'), 'C')$$, 'non rilasciata', 'revisione inesistente rifiutata');
select t.ok(exists (select 1 from revision_tree(t.id('111-0001-D')) where filename = '111-0001.SLDPRT' and version = 11),
            'il disegno porta con se'' il modello');

-- Scritture vietate ----------------------------------------------------------
select t.fails($$insert into item_revisions (item_id, revision) values (t.id('120-0002'), 'Z')$$, 'permission denied', 'revisioni non scrivibili direttamente');
select t.fails($$delete from file_refs$$, 'permission denied', 'riferimenti non scrivibili direttamente');
select t.fails($$select write_file_refs(t.vid('120-0001', 12), '[]')$$, 'permission denied', 'write_file_refs interna');

-- Riferimenti al check-in ----------------------------------------------------
select set_config('pdm.user_login', 'acavani', false);
select t.ok(check_in(t.id('120-0001'), repeat('a', 64), 1000, 'Diametro 20') = 13, 'check-in albero v13');
select check_out(t.id('120-0001-D'));
select t.fails($$select check_in(t.id('120-0001-D'), repeat('b', 64), 1000, null, '[{"filename": "inesistente.sldprt"}]')$$,
               'non presente nel vault', 'riferimento a file sconosciuto rifiutato');
select t.fails($$select check_in(t.id('120-0001-D'), repeat('b', 64), 1000, null, '[{"filename": "120-0001-D.SLDDRW"}]')$$,
               'se stesso', 'riferimento a se stesso rifiutato');
select t.fails($$select check_in(t.id('120-0001-D'), repeat('b', 64), 1000, null, '[{"filename": "120-0001.SLDPRT", "version": 99}]')$$,
               'inesistente', 'versione inesistente rifiutata');
select t.ok(check_in(t.id('120-0001-D'), repeat('b', 64), 1000, 'Quote diametro', '[{"filename": "120-0001.sldprt"}]') = 7,
            'check-in disegno v7 con riferimento');
select t.ok((select string_agg(filename || ' v' || version, ', ' order by level) from revision_tree(t.id('120-0001-D')))
            = '120-0001-D.SLDDRW v7, 120-0001.SLDPRT v13', 'il disegno v7 punta all''albero v13');
select t.ok((select revision from file_versions where id = t.vid('120-0001-D', 7)) = 'C', 'versione nuova nella revisione corrente');

-- Controlli di rilascio ------------------------------------------------------
select transition_item(t.id('120-0001-D'), 'review');
select set_config('pdm.user_login', 'lbianchi', false);
select t.fails($$select transition_item(t.id('120-0001-D'), 'rel')$$, 'non rilasciati 120-0001 (wip)', 'disegno bloccato se il modello non e'' rilasciato');
select t.fails($$select transition_item(t.id('100-0001'), 'rel')$$, 'Rilascio bloccato', 'assieme bloccato se un componente non e'' rilasciato');
select set_config('pdm.user_login', 'acavani', false);
select transition_item(t.id('120-0001'), 'review');
select set_config('pdm.user_login', 'lbianchi', false);
select transition_item(t.id('120-0001'), 'rel');
select transition_item(t.id('120-0001-D'), 'rel');
select t.ok((select v.version from item_revisions r join file_versions v on v.id = r.file_version_id
             where r.item_id = t.id('120-0001') and r.revision = 'C') = 13, 'rilascio rev C registra la v13');
select t.ok((select released_by = (current_app_user()).id from item_revisions where item_id = t.id('120-0001') and revision = 'C'),
            'rilascio registra chi ha rilasciato');

-- Nuova revisione ------------------------------------------------------------
select set_config('pdm.user_login', 'acavani', false);
select transition_item(t.id('120-0001'), 'newrev');
select t.ok((select revision from items where id = t.id('120-0001')) = 'D', 'nuova revisione D');
select t.ok((select r.created_by = (current_app_user()).id and r.file_version_id is null
             from item_revisions r where r.item_id = t.id('120-0001') and r.revision = 'D'), 'riga rev D creata, non rilasciata');
select check_out(t.id('120-0001'));
select t.ok(check_in(t.id('120-0001'), repeat('c', 64), 1000) = 14, 'check-in v14');
select t.ok((select revision from file_versions where id = t.vid('120-0001', 14)) = 'D', 'v14 appartiene alla rev D');
select t.ok((select string_agg(filename || ' v' || version, ', ') from revision_tree(t.id('120-0001'), 'C')) = '120-0001.SLDPRT v13',
            'la rev C resta alla v13');
select t.ok((select string_agg(filename || ' v' || version, ', ') from revision_tree(t.id('120-0001'))) = '120-0001.SLDPRT v14',
            'senza revisione: la versione corrente');

-- Nuovo assieme con riferimenti ----------------------------------------------
select t.ok(add_file('140-0001', 'Gruppo albero', 'ASM', '140-0001.SLDASM', '\\PDM\Pompe\', repeat('d', 64), 500,
                     p_refs => '[{"filename": "120-0001.SLDPRT", "version": 13}, {"filename": "150-0002.SLDPRT"}]') is not null,
            'add_file con riferimenti');
select t.ok((select string_agg(filename || ' v' || version, ', ' order by level, filename) from revision_tree(t.id('140-0001')))
            = '140-0001.SLDASM v1, 120-0001.SLDPRT v13, 150-0002.SLDPRT v1', 'albero del nuovo assieme');
select t.ok((select count(*) from item_revisions where item_id = t.id('140-0001') and revision = 'A') = 1, 'nuovo articolo nasce in rev A');

-- Albero ultima versione ----------------------------------------------------
-- 140-0001 v1 riferisce 120-0001 v13; ora 120-0001 e' alla v14
select t.ok((select string_agg(filename || ' v' || version, ', ' order by level, filename) from latest_tree(t.id('140-0001')))
            = '140-0001.SLDASM v1, 120-0001.SLDPRT v14, 150-0002.SLDPRT v1', 'latest_tree prende le versioni correnti');
select t.ok((select string_agg(filename || ' v' || version, ', ' order by level, filename) from revision_tree(t.id('140-0001')))
            = '140-0001.SLDASM v1, 120-0001.SLDPRT v13, 150-0002.SLDPRT v1', 'revision_tree resta sulle versioni registrate');

-- Obsolescenza ---------------------------------------------------------------
select set_config('pdm.user_login', 'lbianchi', false);
select transition_item(t.id('120-0002'), 'obs');
select t.ok((select obsoleted_at is not null from item_revisions where item_id = t.id('120-0002') and revision = 'A'), 'obsolescenza registrata');

reset role;
