# Protocole ClipSync (v0 — MVP)

Deux canaux :

- **WebSocket** `ws(s)://<host>/ws` — présence, relais des clips, accusés. Messages JSON.
- **HTTP** `http(s)://<host>` — upload/download temporaire des images.

Toutes les valeurs de contenu (`text`, octets d'image) sont destinées à devenir
**opaques** (chiffrées de bout en bout) : le serveur route sans lire. Voir §5.

---

## 1. Modèle d'appairage (MVP)

- Un **compte** est identifié par un `accountId` + un **secret partagé** (`token`).
- Le **premier appareil** qui se connecte avec un `accountId` **définit** le secret.
  Les appareils suivants doivent présenter le **même** secret, sinon rejet.
- Chaque **appareil** a un `deviceId` stable, un `deviceName` lisible et un `platform`
  (`windows` | `ios` | `mac` | …).

> MVP : registre en mémoire côté serveur (perdu au redémarrage).
> Cible : appairage par QR code, une paire de clés par appareil, révocation (§5).

---

## 2. WebSocket — client → serveur

### `hello` (obligatoire en premier)
```json
{ "type": "hello", "accountId": "acc-123", "token": "secret-partagé",
  "deviceId": "pc-portable", "deviceName": "PC Portable", "platform": "windows" }
```

### `heartbeat` (toutes ~30 s)
```json
{ "type": "heartbeat" }
```

### `list-devices`
```json
{ "type": "list-devices" }
```

### `clip` — envoyer un élément de presse-papiers
```json
{ "type": "clip", "messageId": "uuid",
  "contentType": "text",            // "text" | "image"
  "text": "contenu si texte",       // si contentType = text
  "fileId": "uuid-fichier",         // si contentType = image (cf. HTTP §4)
  "fileType": "image/png",
  "targets": "all",                 // "all" | "default" | ["deviceId", ...]
  "meta": { "width": 1920, "height": 1080 } }
```
- `targets` : `"all"` = tous les autres appareils en ligne · `"default"` = 1er en ligne
  (MVP) · tableau de `deviceId` = ciblage explicite. Les appareils **hors ligne** sont ignorés.

### `applied` — le destinataire confirme l'écriture dans son presse-papiers
```json
{ "type": "applied", "messageId": "uuid", "to": "deviceId-expéditeur", "success": true }
```
- `to` = le `deviceId` présent dans `clip.from.deviceId` du message reçu.

---

## 3. WebSocket — serveur → client

### `welcome` (réponse à `hello`)
```json
{ "type": "welcome", "deviceId": "pc-portable",
  "devices": [ { "deviceId": "iphone-1", "name": "iPhone de Pierre",
                 "platform": "ios", "online": true, "lastSeen": 1690000000000 } ] }
```

### `devices` (diffusé à chaque changement de présence + périodiquement)
```json
{ "type": "devices", "devices": [ { "deviceId": "...", "name": "...",
  "platform": "...", "online": true, "lastSeen": 1690000000000 } ] }
```
La liste **exclut** l'appareil destinataire lui-même.

### `clip` (relais vers la/les cible(s))
```json
{ "type": "clip", "messageId": "uuid", "contentType": "text|image",
  "text": "…", "fileId": "…", "fileType": "image/png", "meta": {},
  "from": { "deviceId": "iphone-1", "name": "iPhone de Pierre" }, "ts": 1690000000000 }
```

### `sent` (accusé « distribué » renvoyé à l'expéditeur)
```json
{ "type": "sent", "messageId": "uuid", "delivered": 1, "targets": ["pc-portable"] }
```
> `sent` = le serveur a poussé le clip aux cibles en ligne — **pas encore** collé.

### `applied` (accusé « prêt à coller » relayé à l'expéditeur d'origine)
```json
{ "type": "applied", "messageId": "uuid",
  "by": { "deviceId": "pc-portable", "name": "PC Portable" },
  "success": true, "ts": 1690000000000 }
```

### `error`
```json
{ "type": "error", "message": "bad pairing secret" }
```

---

## 4. HTTP — images

En-têtes d'authentification requis sur les deux routes :
`x-account-id: <accountId>` et `x-token: <secret>`.

### `POST /files` — upload
- `Content-Type: application/octet-stream`, corps = octets bruts de l'image.
- En-tête `x-file-type` = type MIME d'origine (ex. `image/png`).
- Réponse : `{ "fileId": "uuid", "ttlMs": 300000 }`

### `GET /files/:id` — download
- Renvoie les octets avec le `Content-Type` d'origine.
- `404` si inconnu ou expiré (TTL 5 min). `403` si le fichier appartient à un autre compte.

### `GET /health`
- `{ "ok": true, "ts": 1690000000000 }`

---

## 5. Sécurité — feuille de route (post-MVP)

- **Chiffrement de bout en bout** : `text` et octets d'image chiffrés côté client.
  Le contenu est chiffré une fois avec une clé de contenu aléatoire, elle-même
  chiffrée **pour chaque appareil cible** → permet « envoyer à tous » sans ré-upload,
  sans que le serveur puisse lire.
- **Appairage par QR code** : transfère `accountId` + secret + clé publique de l'appareil.
- **Révocation** : invalider la clé d'un appareil ; l'ancien jeton devient inutilisable.
- **Transport** : WSS + HTTPS (TLS terminé par Nginx sur le VPS).

---

## 6. Cycle de vie d'un envoi (exemple image, iPhone → PC)

```
iPhone                 Serveur                    PC
  |  POST /files (img) --->|                        |
  |<-- { fileId } ---------|                        |
  |  clip{fileId,targets} ->|  relaie clip --------->|
  |<-- sent{delivered:1} --|                        | GET /files/:id --->|
  |                        |<--- (télécharge) -------|
  |                        |                        | écrit presse-papiers
  |                        |<-- applied{to:iphone} --|
  |<-- applied{success} ---|                        |
  ✓ « Copié sur PC Portable »                        ✓ notif « prêt à coller »
```
