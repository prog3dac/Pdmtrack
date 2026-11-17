// Forme delle risposte dell'API (snake_case, come le serializza ASP.NET Core)
export type ItemType = 'PRT' | 'ASM' | 'DRW';
export type ItemState = 'wip' | 'review' | 'rel' | 'obs';

export interface Me { id: string; login: string; name: string; role: string }

export interface Item {
  id: string; code: string; description: string; type: ItemType; revision: string; state: ItemState;
  material: string | null; mass_kg: number; filename: string | null; folder_path: string | null;
  current_version: number | null; size_bytes: number | null; locked_by: string | null; locked_at: string | null;
  ref_code: string | null;
}
export interface Revision {
  revision: string; version: number | null; created_at: string; released_by: string | null;
  released_at: string | null; obsoleted_at: string | null;
}
export interface Version {
  version: number; revision: string; sha256: string; size_bytes: number; comment: string | null;
  created_by: string | null; created_at: string;
}
export interface ItemEvent { text: string; user: string | null; created_at: string }
export interface ItemDetail { item: Item; revisions: Revision[]; versions: Version[]; events: ItemEvent[] }
export interface BomLine {
  level: number; path: string; code: string; description: string; qty: number; total_qty: number;
  state: ItemState; unit_mass_kg: number;
}
export interface Bom { code: string; mass_kg: number; lines: BomLine[] }
export interface WhereUsed { parent_code: string; parent_description: string; parent_state: ItemState; qty: number }
