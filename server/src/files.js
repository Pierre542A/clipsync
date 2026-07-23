// Stockage temporaire des images (et fichiers) qui transitent entre appareils.
// TTL court : supprimé automatiquement après expiration (voir config.fileTtlMs).
// Le serveur ne conserve jamais durablement le contenu.

import { randomUUID } from 'node:crypto';
import { config } from './config.js';

export class FileStore {
  constructor() {
    /** @type {Map<string, {buf:Buffer, contentType:string, meta:object, expiresAt:number}>} */
    this.files = new Map();
    const t = setInterval(() => this.sweep(), 30_000);
    t.unref?.(); // n'empêche pas le process de se terminer
  }

  put(buf, contentType, meta = {}) {
    const id = randomUUID();
    this.files.set(id, {
      buf,
      contentType: contentType || 'application/octet-stream',
      meta,
      expiresAt: Date.now() + config.fileTtlMs,
    });
    return id;
  }

  get(id) {
    const f = this.files.get(id);
    if (!f) return null;
    if (Date.now() > f.expiresAt) {
      this.files.delete(id);
      return null;
    }
    return f;
  }

  sweep() {
    const now = Date.now();
    for (const [id, f] of this.files) if (now > f.expiresAt) this.files.delete(id);
  }
}
