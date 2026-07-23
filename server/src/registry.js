// Registre en mémoire des comptes, appareils et de leur présence.
// MVP : tout est en RAM (réinitialisé au redémarrage du serveur).

import { config } from './config.js';

export class Registry {
  constructor() {
    /** @type {Map<string, {id:string, secret:string|null, devices:Map}>} */
    this.accounts = new Map();
  }

  #account(accountId) {
    let acc = this.accounts.get(accountId);
    if (!acc) {
      acc = { id: accountId, secret: null, devices: new Map() };
      this.accounts.set(accountId, acc);
    }
    return acc;
  }

  // Vérifie/initialise le secret partagé du compte.
  // Le 1er appareil qui se connecte définit le secret ; les suivants doivent le fournir.
  authenticate(accountId, token) {
    const acc = this.#account(accountId);
    if (acc.secret === null) acc.secret = token;
    return acc.secret === token;
  }

  // Enregistre (ou met à jour) un appareil et l'attache à sa socket.
  connect({ accountId, deviceId, deviceName, platform }, socket) {
    const acc = this.#account(accountId);
    let dev = acc.devices.get(deviceId);
    if (!dev) {
      dev = { id: deviceId, name: deviceName, platform, socket: null, lastSeen: 0 };
      acc.devices.set(deviceId, dev);
    }
    if (deviceName) dev.name = deviceName;
    if (platform) dev.platform = platform;
    dev.socket = socket;
    dev.lastSeen = Date.now();
    socket._clip = { accountId, deviceId };
    return dev;
  }

  touch(socket) {
    const ref = socket._clip;
    if (!ref) return;
    const dev = this.accounts.get(ref.accountId)?.devices.get(ref.deviceId);
    if (dev) dev.lastSeen = Date.now();
  }

  disconnect(socket) {
    const ref = socket._clip;
    if (!ref) return null;
    const dev = this.accounts.get(ref.accountId)?.devices.get(ref.deviceId);
    if (dev && dev.socket === socket) {
      dev.socket = null;
      dev.lastSeen = Date.now();
    }
    return ref;
  }

  isOnline(dev) {
    return !!dev.socket && (Date.now() - dev.lastSeen) < config.offlineAfterMs;
  }

  listDevices(accountId, exceptId = null) {
    const acc = this.accounts.get(accountId);
    if (!acc) return [];
    return [...acc.devices.values()]
      .filter((d) => d.id !== exceptId)
      .map((d) => ({
        deviceId: d.id,
        name: d.name,
        platform: d.platform,
        online: this.isOnline(d),
        lastSeen: d.lastSeen,
      }));
  }

  getDevice(accountId, deviceId) {
    return this.accounts.get(accountId)?.devices.get(deviceId) ?? null;
  }

  // Résout des cibles logiques ("all" / "default" / [ids]) en appareils EN LIGNE.
  resolveTargets(accountId, senderId, targets) {
    const acc = this.accounts.get(accountId);
    if (!acc) return [];
    const online = [...acc.devices.values()].filter(
      (d) => d.id !== senderId && this.isOnline(d),
    );
    if (targets === 'all' || targets == null) return online;
    if (targets === 'default') return online.slice(0, 1); // MVP : 1er en ligne
    const wanted = new Set(Array.isArray(targets) ? targets : [targets]);
    return online.filter((d) => wanted.has(d.id));
  }

  broadcastDevices(accountId) {
    const acc = this.accounts.get(accountId);
    if (!acc) return;
    for (const dev of acc.devices.values()) {
      if (!dev.socket) continue;
      safeSend(
        dev.socket,
        JSON.stringify({ type: 'devices', devices: this.listDevices(accountId, dev.id) }),
      );
    }
  }
}

export function safeSend(socket, data) {
  try {
    if (socket && socket.readyState === 1) socket.send(data); // 1 = OPEN
  } catch {
    /* socket fermée entre-temps : on ignore */
  }
}
