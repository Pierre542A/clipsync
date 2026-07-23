import Foundation
import UIKit
import CryptoKit

// Client réseau : WebSocket permanent (présence + réception en direct quand l'app
// est ouverte) + HTTP pour les images. Contenu chiffré de bout en bout.
@MainActor
final class RelayClient: ObservableObject {
    @Published var connected = false
    @Published var devices: [DeviceInfo] = []
    @Published var lastEvent = "—"

    private var cfg: Config
    private let session = URLSession(configuration: .default)
    private var task: URLSessionWebSocketTask?
    private var running = false
    private var heartbeat: Task<Void, Never>?

    private var authToken: String { ClipCrypto.authToken(cfg.secret) }
    private var encKey: SymmetricKey { ClipCrypto.encKey(cfg.secret) }

    init(cfg: Config) { self.cfg = cfg }

    func update(cfg: Config) {
        self.cfg = cfg
        task?.cancel(with: .goingAway, reason: nil)
    }

    func start() {
        guard !running else { return }
        running = true
        Task { await connectLoop() }
    }

    func stop() {
        running = false
        heartbeat?.cancel()
        task?.cancel(with: .goingAway, reason: nil)
        connected = false
    }

    // MARK: - Connexion + reconnexion

    private func connectLoop() async {
        while running {
            guard cfg.isConfigured, let url = URL(string: cfg.serverURL) else {
                lastEvent = "À configurer (Réglages)"
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                continue
            }
            do { try await connectOnce(url: url) }
            catch { lastEvent = "Hors ligne" }
            connected = false
            heartbeat?.cancel()
            try? await Task.sleep(nanoseconds: 3_000_000_000)
        }
    }

    private func connectOnce(url: URL) async throws {
        let t = session.webSocketTask(with: url)
        task = t
        t.resume()

        try await sendJSON([
            "type": "hello",
            "accountId": cfg.accountId,
            "token": authToken,
            "deviceId": cfg.deviceId,
            "deviceName": cfg.deviceName,
            "platform": "ios",
        ], over: t)

        connected = true
        lastEvent = "Connecté"
        startHeartbeat(t)

        while running {
            let message = try await t.receive()
            switch message {
            case .string(let s): handle(s)
            case .data(let d): if let s = String(data: d, encoding: .utf8) { handle(s) }
            @unknown default: break
            }
        }
    }

    private func startHeartbeat(_ t: URLSessionWebSocketTask) {
        heartbeat?.cancel()
        heartbeat = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 30_000_000_000)
                try? await self?.sendJSON(["type": "heartbeat"], over: t)
            }
        }
    }

    // MARK: - Réception (déchiffrement)

    private func handle(_ text: String) {
        guard let data = text.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = obj["type"] as? String else { return }

        switch type {
        case "welcome", "devices":
            if let arr = obj["devices"],
               let d = try? JSONSerialization.data(withJSONObject: arr),
               let list = try? JSONDecoder().decode([DeviceInfo].self, from: d) {
                devices = list
            }
        case "clip":
            receiveClip(obj)
        case "sent":
            lastEvent = "Envoyé ✓"
        case "applied":
            let name = (obj["by"] as? [String: Any])?["name"] as? String ?? "l'autre appareil"
            lastEvent = "Collé sur \(name) ✓"
        case "error":
            lastEvent = "Erreur : \(obj["message"] as? String ?? "?")"
        default:
            break
        }
    }

    private func receiveClip(_ obj: [String: Any]) {
        let ct = obj["contentType"] as? String
        let from = obj["from"] as? [String: Any]
        let fromName = from?["name"] as? String ?? "un appareil"
        let fromId = from?["deviceId"] as? String
        let messageId = obj["messageId"] as? String

        if ct == "text", let enc = obj["text"] as? String, let text = ClipCrypto.decryptText(enc, key: encKey) {
            UIPasteboard.general.string = text
            lastEvent = "Reçu de \(fromName) — dans le presse-papiers"
            ackApplied(messageId: messageId, to: fromId)
        } else if ct == "image", let fileId = obj["fileId"] as? String {
            Task {
                if let blob = await downloadFile(fileId),
                   let data = ClipCrypto.decrypt(blob, key: encKey),
                   let img = UIImage(data: data) {
                    UIPasteboard.general.image = img
                    lastEvent = "Image reçue de \(fromName) — dans le presse-papiers"
                    ackApplied(messageId: messageId, to: fromId)
                }
            }
        }
    }

    private func ackApplied(messageId: String?, to: String?) {
        guard let messageId, let to, let t = task else { return }
        Task { try? await sendJSON(["type": "applied", "messageId": messageId, "to": to, "success": true], over: t) }
    }

    // MARK: - Envoi (chiffrement)

    func sendPasteboard() async {
        let pb = UIPasteboard.general
        if let text = pb.string, !text.isEmpty {
            await sendText(text)
        } else if let image = pb.image, let png = image.pngData() {
            await sendImage(png)
        } else {
            lastEvent = "Presse-papiers vide"
        }
    }

    func sendText(_ text: String) async {
        guard let t = task else { lastEvent = "Non connecté"; return }
        guard let ct = ClipCrypto.encryptText(text, key: encKey) else { lastEvent = "Chiffrement échoué"; return }
        try? await sendJSON([
            "type": "clip", "messageId": UUID().uuidString,
            "contentType": "text", "text": ct, "enc": "v1", "targets": "all",
        ], over: t)
        lastEvent = "Texte envoyé…"
    }

    func sendImage(_ png: Data) async {
        guard let t = task else { lastEvent = "Non connecté"; return }
        lastEvent = "Envoi de l'image…"
        guard let blob = ClipCrypto.encrypt(png, key: encKey) else { lastEvent = "Chiffrement échoué"; return }
        guard let fileId = await uploadBlob(blob) else { lastEvent = "Échec upload image"; return }
        try? await sendJSON([
            "type": "clip", "messageId": UUID().uuidString,
            "contentType": "image", "fileId": fileId, "fileType": "image/png", "enc": "v1", "targets": "all",
        ], over: t)
        lastEvent = "Image envoyée…"
    }

    // MARK: - HTTP (contenu déjà chiffré)

    private func uploadBlob(_ data: Data) async -> String? {
        guard let url = URL(string: cfg.httpURL + "/files") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        req.setValue(cfg.accountId, forHTTPHeaderField: "x-account-id")
        req.setValue(authToken, forHTTPHeaderField: "x-token")
        req.setValue("application/octet-stream", forHTTPHeaderField: "x-file-type")
        req.httpBody = data
        guard let (respData, resp) = try? await session.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let obj = try? JSONSerialization.jsonObject(with: respData) as? [String: Any] else { return nil }
        return obj["fileId"] as? String
    }

    private func downloadFile(_ fileId: String) async -> Data? {
        guard let url = URL(string: cfg.httpURL + "/files/" + fileId) else { return nil }
        var req = URLRequest(url: url)
        req.setValue(cfg.accountId, forHTTPHeaderField: "x-account-id")
        req.setValue(authToken, forHTTPHeaderField: "x-token")
        guard let (data, resp) = try? await session.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return data
    }

    // MARK: - util

    @discardableResult
    private func sendJSON(_ obj: [String: Any], over t: URLSessionWebSocketTask) async throws -> Bool {
        let data = try JSONSerialization.data(withJSONObject: obj)
        let str = String(data: data, encoding: .utf8) ?? "{}"
        try await t.send(.string(str))
        return true
    }
}
