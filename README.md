# ClipSync

Presse-papiers **partagé** entre un iPhone et plusieurs PC Windows.
Synchronise **texte et images dans les deux sens**, **partout** (même hors du même réseau),
avec statut de présence des appareils et confirmation « prêt à coller ».

> Projet perso — MVP en cours de construction.

## Architecture

```
        ┌────────────────────────┐
        │   Serveur relais       │   Node.js + Fastify + WebSocket
        │   (présence + relais)  │   stockage temporaire chiffré des images
        └───────────┬────────────┘
                    │  WSS (commandes/présence)  +  HTTPS (images)
   ┌────────────────┼─────────────────┐
   │                │                 │
 iPhone         PC portable        PC fixe
 (Swift)        (C#/.NET)          (C#/.NET)
```

- **Une fois appairés, les appareils n'ont plus besoin d'être sur le même réseau** :
  tout transite par le serveur relais (HTTPS/WSS), donc iPhone en 4G, PC à la maison, PC au travail.
- **Windows → iPhone** : détection automatique de la copie → notification native iOS (`Copier` / `Voir`).
- **iPhone → Windows** : déclenché par un geste (triple-tap au dos, menu Partager, Centre de contrôle),
  car iOS n'autorise pas la surveillance continue du presse-papiers en arrière-plan.

## Composants

| Dossier    | Rôle                        | Stack                                   | État |
|------------|-----------------------------|-----------------------------------------|------|
| `server/`  | Relais + **sert la PWA**    | Node.js, Fastify, WebSocket, static     | ✅ **déployé** |
| `server/public/` | **PWA iPhone (sans Mac)** | HTML/JS + Web Crypto (E2E)         | ✅ **live** — `https://clip.lateliercbd.com` |
| `windows/` | Client PC (zone de notif)   | C#/.NET 8 (WinForms tray, WebSocket)    | ✅ texte + images |
| `ios/`     | App iPhone **native**       | Swift, SwiftUI, App Intents             | ✅ code (compile sur Mac ; la PWA est l'alternative sans Mac) |

## Démarrer le serveur

```bash
cd server
npm install
npm start
# → http://localhost:8787   ws://localhost:8787/ws
```

Variables d'env utiles : `PORT` (def. 8787), `HOST` (def. 0.0.0.0).

## Sécurité (feuille de route)

- Chiffrement de bout en bout : le serveur ne voit jamais le contenu en clair (payloads opaques).
- Appairage par QR code + secret partagé, révocation d'appareil.
- Images stockées temporairement (TTL 5 min) puis supprimées.

Voir [`docs/PROTOCOL.md`](docs/PROTOCOL.md) pour le protocole de messages.
