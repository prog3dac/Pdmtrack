'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '@/lib/api';
import type { Bom, Item, ItemDetail, ItemState, ItemType, Me, WhereUsed } from '@/lib/types';

const STATES: Record<ItemState, [string, string]> = {
  wip: ['In lavorazione', 'info'],
  review: ['In approvazione', 'warn'],
  rel: ['Rilasciato', 'ok'],
  obs: ['Obsoleto', 'neutral'],
};
const TYPES: Record<ItemType, string> = { PRT: 'Parte', ASM: 'Assieme', DRW: 'Disegno' };
const PLURAL: Record<ItemType, string> = { PRT: 'Parti', ASM: 'Assiemi', DRW: 'Disegni' };
const ROLES: Record<string, string> = { designer: 'Progettista', approver: 'Approvatore', admin: 'Amministratore' };

type View = 'all' | 'locked' | 'review';
const VIEWS: { key: View; label: string; title: string; sub: string }[] = [
  { key: 'all', label: 'Articoli', title: 'Articoli', sub: 'Parti, assiemi e disegni nel vault' },
  { key: 'locked', label: 'In check-out', title: 'File in check-out', sub: 'File bloccati per modifica' },
  { key: 'review', label: 'In approvazione', title: 'In approvazione', sub: 'Articoli in attesa di rilascio' },
];
type Tab = 'info' | 'bom' | 'used' | 'versions' | 'revisions' | 'history';

const fmtDate = (s: string | null) => (s ? new Date(s).toLocaleDateString('it-IT') : '');
const fmtDateTime = (s: string) =>
  new Date(s).toLocaleString('it-IT', { dateStyle: 'short', timeStyle: 'short' });
const fmtKg = (n: number) => n.toLocaleString('it-IT', { maximumFractionDigits: 3 }) + ' kg';
const fmtSize = (b: number | null) =>
  b == null ? '' : b < 1048576 ? `${Math.max(1, Math.round(b / 1024))} kB` : `${(b / 1048576).toLocaleString('it-IT', { maximumFractionDigits: 1 })} MB`;
const initials = (name: string) => name.split(/[\s.]+/).filter(Boolean).map((p) => p[0]).join('').slice(0, 2).toUpperCase();

function StatePill({ state }: { state: ItemState }) {
  const [label, tone] = STATES[state];
  return <span className={`pill ${tone}`}>{label}</span>;
}

function TypeBadge({ type }: { type: ItemType }) {
  return <span className={`tb ${type}`} title={TYPES[type]}>{type}</span>;
}

const LockIcon = () => (
  <svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true">
    <rect x="3" y="7" width="10" height="7" rx="1.5" />
    <path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2" />
  </svg>
);

function readCodeFromUrl() {
  if (typeof window === 'undefined') return null;
  return new URLSearchParams(window.location.search).get('code');
}

export default function Home() {
  const [me, setMe] = useState<Me | null>(null);
  const [items, setItems] = useState<Item[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>('all');
  const [query, setQuery] = useState('');
  const [type, setType] = useState<ItemType | ''>('');
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => {
    setSelected(readCodeFromUrl());
    Promise.all([api<Me>('/me'), api<Item[]>('/items?limit=1000')])
      .then(([m, list]) => { setMe(m); setItems(list); })
      .catch((e: Error) => setError(e.message));
  }, []);

  const select = useCallback((code: string | null) => {
    setSelected(code);
    const url = new URL(window.location.href);
    if (code) url.searchParams.set('code', code); else url.searchParams.delete('code');
    window.history.replaceState(null, '', url);
  }, []);

  const counts = useMemo(() => ({
    all: items?.length ?? 0,
    locked: items?.filter((i) => i.locked_by).length ?? 0,
    review: items?.filter((i) => i.state === 'review').length ?? 0,
  }), [items]);

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (items ?? []).filter((i) =>
      (view !== 'locked' || i.locked_by) &&
      (view !== 'review' || i.state === 'review') &&
      (!type || i.type === type) &&
      (!q || i.code.toLowerCase().includes(q) || i.description.toLowerCase().includes(q) ||
        (i.material ?? '').toLowerCase().includes(q)));
  }, [items, view, query, type]);

  const v = VIEWS.find((x) => x.key === view)!;

  return (
    <div className="app">
      <aside className="side">
        <div className="brand">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" aria-hidden="true">
            <path d="M3 3h13l5 5v13H3z" fill="#8E94FF" />
            <path d="M16 3v5h5" fill="#1B1E2B" opacity=".35" />
            <path d="M8 12h8M8 16h5" stroke="#1B1E2B" strokeWidth="1.6" strokeLinecap="round" />
          </svg>
          <span>Pdmtrack</span>
        </div>
        <nav className="nav" aria-label="Sezioni">
          {VIEWS.map((x) => (
            <button key={x.key} aria-current={view === x.key} onClick={() => setView(x.key)}>
              <span>{x.label}</span>
              <span className="cnt">{counts[x.key]}</span>
            </button>
          ))}
        </nav>
        <div className="foot">
          {me && (
            <div className="me">
              <div className="av">{initials(me.name)}</div>
              <div>{me.name}<br /><span style={{ opacity: 0.7 }}>{ROLES[me.role] ?? me.role}</span></div>
            </div>
          )}
          <div style={{ opacity: 0.6 }}>Consultazione: le modifiche si fanno dal client Windows</div>
        </div>
      </aside>

      <main className="main">
        <header className="top">
          <div>
            <h1>{v.title}</h1>
            <div className="sub">{v.sub}</div>
          </div>
          <div className="tools">
            <label className="search">
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
                <circle cx="7" cy="7" r="4.5" /><path d="M10.5 10.5 14 14" />
              </svg>
              <input type="search" placeholder="Codice, descrizione o materiale" value={query}
                     onChange={(e) => setQuery(e.target.value)} aria-label="Cerca" />
            </label>
            <div className="chips" role="group" aria-label="Tipo">
              {(['', 'ASM', 'PRT', 'DRW'] as const).map((t) => (
                <button key={t || 'all'} className="chip" aria-pressed={type === t} onClick={() => setType(t)}>
                  {t ? PLURAL[t] : 'Tutti'}
                </button>
              ))}
            </div>
          </div>
        </header>

        <div className={'work' + (selected ? ' open' : '')}>
          <section className="pane">
            {error && <div className="empty" role="alert">{error}</div>}
            {!error && !items && <div className="empty">Caricamento…</div>}
            {items && (
              <>
                <div className="stats">
                  <span><b>{visible.length}</b> articoli</span>
                  <span><b>{visible.filter((i) => i.state === 'rel').length}</b> rilasciati</span>
                  <span><b>{visible.filter((i) => i.locked_by).length}</b> in check-out</span>
                </div>
                {visible.length === 0 ? (
                  <div className="empty">Nessun articolo corrisponde alla ricerca</div>
                ) : (
                  <div className="tw">
                    <table className="tbl">
                      <thead>
                        <tr>
                          <th>Codice</th><th>Descrizione</th><th>Tipo</th><th>Rev</th><th>Stato</th>
                          <th className="hm">Versione</th><th className="hm">Check-out</th>
                        </tr>
                      </thead>
                      <tbody>
                        {visible.map((i) => (
                          <tr key={i.id} data-act="open" className={selected === i.code ? 'on' : ''}
                              onClick={() => select(i.code)} tabIndex={0}
                              onKeyDown={(e) => { if (e.key === 'Enter') select(i.code); }}>
                            <td className="mono">{i.code}</td>
                            <td className="d"><div className="ellip">{i.description}</div></td>
                            <td><TypeBadge type={i.type} /></td>
                            <td className="rev">{i.revision}</td>
                            <td><StatePill state={i.state} /></td>
                            <td className="hm mono">{i.current_version ? `v${i.current_version}` : ''}</td>
                            <td className="hm">
                              {i.locked_by && (
                                <span className={'lock' + (i.locked_by === me?.name ? ' me' : '')}>
                                  <LockIcon />{i.locked_by}
                                </span>
                              )}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </>
            )}
          </section>
          {selected && <DetailPanel code={selected} onSelect={select} onClose={() => select(null)} />}
        </div>
      </main>
    </div>
  );
}

function DetailPanel({ code, onSelect, onClose }: {
  code: string; onSelect: (code: string) => void; onClose: () => void;
}) {
  const [detail, setDetail] = useState<ItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('info');

  useEffect(() => {
    setDetail(null); setError(null);
    api<ItemDetail>(`/items/${encodeURIComponent(code)}`).then(setDetail).catch((e: Error) => setError(e.message));
  }, [code]);

  useEffect(() => {
    if (detail && tab === 'bom' && detail.item.type !== 'ASM') setTab('info');
  }, [detail, tab]);

  const i = detail?.item;
  const tabs: [Tab, string][] = [
    ['info', 'Dettagli'],
    ...(i?.type === 'ASM' ? [['bom', 'Distinta'] as [Tab, string]] : []),
    ['used', 'Usato in'],
    ['versions', 'Versioni'],
    ['revisions', 'Revisioni'],
    ['history', 'Storico'],
  ];

  return (
    <aside className="detail" aria-label={`Dettaglio ${code}`}>
      <div className="dh">
        <div className="row">
          <div style={{ minWidth: 0 }}>
            <div className="mono dim">{code}</div>
            <h2>{i?.description ?? (error ? 'Errore' : 'Caricamento…')}</h2>
          </div>
          <button className="x" onClick={onClose} aria-label="Chiudi">
            <svg width="12" height="12" viewBox="0 0 12 12" stroke="currentColor" strokeWidth="1.6" fill="none"><path d="M2 2l8 8M10 2l-8 8" /></svg>
          </button>
        </div>
        {i && (
          <div className="acts">
            <TypeBadge type={i.type} /><span className="rev">Rev {i.revision}</span><StatePill state={i.state} />
            {i.locked_by && <span className="lock"><LockIcon />In check-out a {i.locked_by}</span>}
          </div>
        )}
        <div className="tabs" role="tablist">
          {tabs.map(([k, label]) => (
            <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)}>{label}</button>
          ))}
        </div>
      </div>
      <div className="db">
        {error && <div className="empty" role="alert">{error}</div>}
        {detail && tab === 'info' && <InfoTab d={detail} onSelect={onSelect} />}
        {detail && tab === 'bom' && <BomTab code={code} onSelect={onSelect} />}
        {detail && tab === 'used' && <WhereUsedTab code={code} onSelect={onSelect} />}
        {detail && tab === 'versions' && <VersionsTab d={detail} />}
        {detail && tab === 'revisions' && <RevisionsTab d={detail} />}
        {detail && tab === 'history' && <HistoryTab d={detail} />}
      </div>
    </aside>
  );
}

function InfoTab({ d, onSelect }: { d: ItemDetail; onSelect: (c: string) => void }) {
  const i = d.item;
  const rows: [string, React.ReactNode][] = [
    ['Tipo', TYPES[i.type]],
    ['File', i.filename && <span className="mono">{i.filename}</span>],
    ['Cartella', i.folder_path && <span className="mono">{i.folder_path}</span>],
    ['Versione corrente', i.current_version && `v${i.current_version} · ${fmtSize(i.size_bytes)}`],
    ['Materiale', i.material],
    ['Massa', i.type === 'PRT' ? fmtKg(i.mass_kg) : null],
    ['Modello di riferimento', i.ref_code && <button className="lk mono" onClick={() => onSelect(i.ref_code!)}>{i.ref_code}</button>],
    ['Check-out', i.locked_by && `${i.locked_by}, ${fmtDateTime(i.locked_at!)}`],
  ];
  return (
    <div className="kv">
      {rows.filter(([, v]) => v).flatMap(([k, v]) => [
        <div key={k + 'k'} className="k">{k}</div>,
        <div key={k + 'v'}>{v}</div>,
      ])}
    </div>
  );
}

function BomTab({ code, onSelect }: { code: string; onSelect: (c: string) => void }) {
  const [bom, setBom] = useState<Bom | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api<Bom>(`/items/${encodeURIComponent(code)}/bom`).then(setBom).catch((e: Error) => setError(e.message));
  }, [code]);
  if (error) return <div className="empty" role="alert">{error}</div>;
  if (!bom) return <div className="empty">Caricamento…</div>;
  if (bom.lines.length === 0) return <div className="empty">Distinta vuota</div>;
  return (
    <>
      <div className="tw bom">
        <table className="tbl">
          <thead><tr><th>Codice</th><th>Descrizione</th><th>Q.tà</th><th>Stato</th></tr></thead>
          <tbody>
            {bom.lines.map((l) => (
              <tr key={l.path}>
                <td style={{ paddingLeft: 10 + (l.level - 1) * 16 }}>
                  <button className="lk mono" onClick={() => onSelect(l.code)}>{l.code}</button>
                </td>
                <td className="d"><div className="ellip">{l.description}</div></td>
                <td className="num">{l.qty.toLocaleString('it-IT')}</td>
                <td><StatePill state={l.state} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="tot"><span>Massa totale</span><span className="num">{fmtKg(bom.mass_kg)}</span></div>
    </>
  );
}

function WhereUsedTab({ code, onSelect }: { code: string; onSelect: (c: string) => void }) {
  const [rows, setRows] = useState<WhereUsed[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api<WhereUsed[]>(`/items/${encodeURIComponent(code)}/where-used`).then(setRows).catch((e: Error) => setError(e.message));
  }, [code]);
  if (error) return <div className="empty" role="alert">{error}</div>;
  if (!rows) return <div className="empty">Caricamento…</div>;
  if (rows.length === 0) return <div className="empty">Non usato in nessun assieme</div>;
  return (
    <div className="tw">
      <table className="tbl">
        <thead><tr><th>Assieme</th><th>Descrizione</th><th>Q.tà</th><th>Stato</th></tr></thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.parent_code}>
              <td><button className="lk mono" onClick={() => onSelect(r.parent_code)}>{r.parent_code}</button></td>
              <td className="d"><div className="ellip">{r.parent_description}</div></td>
              <td className="num">{r.qty.toLocaleString('it-IT')}</td>
              <td><StatePill state={r.parent_state} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function VersionsTab({ d }: { d: ItemDetail }) {
  return (
    <div className="tw">
      <table className="tbl">
        <thead><tr><th>Ver</th><th>Rev</th><th>Commento</th><th>Autore</th><th>Data</th></tr></thead>
        <tbody>
          {d.versions.map((v) => (
            <tr key={v.version}>
              <td className="mono">v{v.version}</td>
              <td className="rev">{v.revision}</td>
              <td className="d"><div className="ellip" title={`SHA-256 ${v.sha256}`}>{v.comment}</div></td>
              <td>{v.created_by}</td>
              <td className="mono">{fmtDate(v.created_at)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RevisionsTab({ d }: { d: ItemDetail }) {
  return (
    <div className="tw">
      <table className="tbl">
        <thead><tr><th>Rev</th><th>Versione</th><th>Rilasciata</th><th>Da</th><th>Obsoleta</th></tr></thead>
        <tbody>
          {[...d.revisions].reverse().map((r) => (
            <tr key={r.revision}>
              <td className="rev">{r.revision}</td>
              <td className="mono">{r.version ? `v${r.version}` : <span className="dim">in lavorazione</span>}</td>
              <td className="mono">{fmtDate(r.released_at)}</td>
              <td>{r.released_by}</td>
              <td className="mono">{fmtDate(r.obsoleted_at)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function HistoryTab({ d }: { d: ItemDetail }) {
  if (d.events.length === 0) return <div className="empty">Nessun evento registrato</div>;
  return (
    <ul className="tl">
      {d.events.map((e, n) => (
        <li key={n}>
          <span className="dt">{fmtDate(e.created_at)}</span>
          <span>{e.text}{e.user && <span className="dim"> · {e.user}</span>}</span>
        </li>
      ))}
    </ul>
  );
}
