// Chiamate all'API. In sviluppo l'utente si imposta con NEXT_PUBLIC_PDM_DEV_USER
// (intestazione X-Pdm-User, accettata dall'API solo in Development); in produzione
// il browser usa il login Windows.
const devUser = process.env.NEXT_PUBLIC_PDM_DEV_USER;

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export async function api<T>(path: string): Promise<T> {
  const r = await fetch('/api' + path, {
    credentials: 'include',
    headers: devUser ? { 'X-Pdm-User': devUser } : undefined,
  });
  if (!r.ok) {
    let msg = `Errore ${r.status}`;
    try { msg = (await r.json()).detail ?? msg; } catch { /* corpo non JSON */ }
    if (r.status === 401) msg = 'Accesso non riuscito: apri la pagina dalla rete aziendale con il tuo account Windows.';
    throw new ApiError(r.status, msg);
  }
  return r.json() as Promise<T>;
}
