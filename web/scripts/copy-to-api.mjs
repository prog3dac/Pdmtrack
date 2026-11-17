// Copia il sito esportato (out/) nella cartella wwwroot dell'API.
import { cpSync, rmSync } from 'node:fs';
const target = new URL('../../api/src/Pdmtrack.Api/wwwroot/', import.meta.url);
rmSync(target, { recursive: true, force: true });
cpSync(new URL('../out/', import.meta.url), target, { recursive: true });
console.log('Sito copiato in', target.pathname);
