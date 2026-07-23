// Serveur relais ClipSync.
//  - WebSocket (/ws)   : présence, relais des messages presse-papiers, accusés.
//  - HTTP (/files)     : upload/download temporaire des images.
// Le serveur route les messages mais est prévu pour ne jamais voir le contenu
// en clair une fois le chiffrement de bout en bout branché (payloads opaques).

import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import Fastify from 'fastify';
import websocket from '@fastify/websocket';
import fastifyStatic from '@fastify/static';
import { config } from './config.js';
import { Registry, safeSend } from './registry.js';
import { FileStore } from './files.js';

const registry = new Registry();
const files = new FileStore();

const app = Fastify({ bodyLimit: config.maxFileBytes });
await app.register(websocket);

// Corps binaire brut pour l'upload d'images.
app.addContentTypeParser('application/octet-stream', { parseAs: 'buffer' }, (_req, body, done) =>
  done(null, body),
);

function authHttp(req) {
  const accountId = req.headers['x-account-id'];
  const token = req.headers['x-token'];
  if (!accountId) return null;
  if (!registry.authenticate(String(accountId), String(token ?? ''))) return null;
  return String(accountId);
}

// --- Santé ------------------------------------------------------------------
app.get('/health', async () => ({ ok: true, ts: Date.now() }));

// --- Upload / download des images -------------------------------------------
app.post('/files', async (req, reply) => {
  const accountId = authHttp(req);
  if (!accountId) return reply.code(401).send({ error: 'unauthorized' });

  const buf = req.body;
  if (!Buffer.isBuffer(buf) || buf.length === 0) return reply.code(400).send({ error: 'empty body' });

  const contentType = String(req.headers['x-file-type'] ?? 'application/octet-stream');
  const id = files.put(buf, contentType, { accountId });
  return { fileId: id, ttlMs: config.fileTtlMs };
});

app.get('/files/:id', async (req, reply) => {
  const accountId = authHttp(req);
  if (!accountId) return reply.code(401).send({ error: 'unauthorized' });

  const f = files.get(req.params.id);
  if (!f) return reply.code(404).send({ error: 'not found or expired' });
  if (f.meta.accountId && f.meta.accountId !== accountId) return reply.code(403).send({ error: 'forbidden' });

  reply.header('content-type', f.contentType);
  return reply.send(f.buf);
});

// --- Envoi d'un clip en HTTP (Raccourci Apple / App Intent / app fermée) -----
// Permet d'envoyer sans WebSocket : idéal pour un déclencheur en arrière-plan.
app.post('/clip', async (req, reply) => {
  const accountId = authHttp(req);
  if (!accountId) return reply.code(401).send({ error: 'unauthorized' });

  const body = req.body ?? {};
  const { contentType, text, fileId, fileType, targets, meta, deviceId, deviceName } = body;
  if (!contentType) return reply.code(400).send({ error: 'missing contentType' });

  const senderId = deviceId ?? 'http-sender';
  const resolved = registry.resolveTargets(accountId, senderId, targets ?? 'all');
  const relayed = JSON.stringify({
    type: 'clip',
    messageId: randomUUID(),
    contentType, // 'text' | 'image'
    text,
    fileId,
    fileType,
    meta: meta ?? {},
    from: { deviceId: senderId, name: deviceName ?? 'iPhone' },
    ts: Date.now(),
  });

  let delivered = 0;
  for (const dev of resolved) {
    safeSend(dev.socket, relayed);
    delivered++;
  }
  return { delivered, targets: resolved.map((t) => t.id) };
});

// --- WebSocket temps réel ---------------------------------------------------
app.register(async (app) => {
  app.get('/ws', { websocket: true }, (socket) => {
    socket.on('message', (raw) => {
      let msg;
      try {
        msg = JSON.parse(raw.toString());
      } catch {
        return;
      }
      handle(socket, msg);
    });
    socket.on('close', () => {
      const ref = registry.disconnect(socket);
      if (ref) registry.broadcastDevices(ref.accountId);
    });
    socket.on('error', () => {});
  });
});

function handle(socket, msg) {
  switch (msg?.type) {
    // Authentification + arrivée en ligne.
    case 'hello': {
      const { accountId, token, deviceId, deviceName, platform } = msg;
      if (!accountId || !deviceId) return safeSend(socket, err('missing accountId/deviceId'));
      if (!registry.authenticate(String(accountId), String(token ?? ''))) {
        return safeSend(socket, err('bad pairing secret'));
      }
      registry.connect({ accountId, deviceId, deviceName, platform }, socket);
      safeSend(
        socket,
        JSON.stringify({
          type: 'welcome',
          deviceId,
          devices: registry.listDevices(accountId, deviceId),
        }),
      );
      registry.broadcastDevices(accountId);
      break;
    }

    // Maintien de présence.
    case 'heartbeat': {
      registry.touch(socket);
      break;
    }

    case 'list-devices': {
      const ref = socket._clip;
      if (ref) {
        safeSend(
          socket,
          JSON.stringify({ type: 'devices', devices: registry.listDevices(ref.accountId, ref.deviceId) }),
        );
      }
      break;
    }

    // Envoi d'un élément de presse-papiers vers une ou plusieurs cibles.
    case 'clip': {
      const ref = socket._clip;
      if (!ref) return safeSend(socket, err('not authenticated'));
      registry.touch(socket);

      const targets = registry.resolveTargets(ref.accountId, ref.deviceId, msg.targets);
      const sender = registry.getDevice(ref.accountId, ref.deviceId);
      const relayed = JSON.stringify({
        type: 'clip',
        messageId: msg.messageId,
        contentType: msg.contentType, // 'text' | 'image'
        text: msg.text, // si texte
        fileId: msg.fileId, // si image (à télécharger via GET /files/:id)
        fileType: msg.fileType,
        meta: msg.meta ?? {},
        from: { deviceId: ref.deviceId, name: sender?.name ?? 'inconnu' },
        ts: Date.now(),
      });

      let delivered = 0;
      for (const dev of targets) {
        safeSend(dev.socket, relayed);
        delivered++;
      }
      // Accusé "distribué" (pas encore "appliqué au presse-papiers").
      safeSend(
        socket,
        JSON.stringify({
          type: 'sent',
          messageId: msg.messageId,
          delivered,
          targets: targets.map((t) => t.id),
        }),
      );
      break;
    }

    // Le destinataire confirme avoir écrit dans son presse-papiers.
    case 'applied': {
      const ref = socket._clip;
      if (!ref) return;
      const origin = registry.getDevice(ref.accountId, msg.to);
      if (origin?.socket) {
        safeSend(
          origin.socket,
          JSON.stringify({
            type: 'applied',
            messageId: msg.messageId,
            by: {
              deviceId: ref.deviceId,
              name: registry.getDevice(ref.accountId, ref.deviceId)?.name,
            },
            success: msg.success !== false,
            ts: Date.now(),
          }),
        );
      }
      break;
    }

    default:
      safeSend(socket, err('unknown type: ' + msg?.type));
  }
}

function err(message) {
  return JSON.stringify({ type: 'error', message });
}

// --- PWA : fichiers statiques servis à la racine (/, /app.js, /crypto.js, …) -
await app.register(fastifyStatic, {
  root: path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'public'),
  prefix: '/',
});

// Diffusion périodique de la présence (rafraîchit aussi les statuts en/hors ligne).
const presenceTimer = setInterval(() => {
  for (const accountId of registry.accounts.keys()) registry.broadcastDevices(accountId);
}, 30_000);
presenceTimer.unref?.();

app
  .listen({ port: config.port, host: config.host })
  .then(() => console.log(`ClipSync relay → http://localhost:${config.port}  (ws://localhost:${config.port}/ws)`))
  .catch((e) => {
    console.error(e);
    process.exit(1);
  });
