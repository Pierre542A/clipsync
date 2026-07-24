// Configuration du serveur relais ClipSync.
// Tout est surchargeable par variables d'environnement.

export const config = {
  port: Number(process.env.PORT ?? 8787),
  host: process.env.HOST ?? '0.0.0.0',

  // Délai (ms) sans heartbeat avant de considérer un appareil hors ligne.
  offlineAfterMs: 90_000,

  // Durée de vie d'une image stockée temporairement (ms).
  fileTtlMs: 5 * 60_000,

  // Taille max d'un transfert (image ou fichier), octets.
  maxFileBytes: 100 * 1024 * 1024,
};
