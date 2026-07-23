import Foundation
import UIKit

// Envoi HTTP autonome (sans WebSocket) — utilisé par l'App Intent / le Raccourci,
// qui peuvent s'exécuter même app fermée. Renvoie un message lisible pour Siri/Raccourcis.
struct ClipSender {
    let cfg: Config

    func sendPasteboard() async -> String {
        let pb = UIPasteboard.general
        if let text = pb.string, !text.isEmpty {
            return await postClip([
                "contentType": "text", "text": text, "targets": "all",
                "deviceName": cfg.deviceName, "deviceId": cfg.deviceId,
            ])
        }
        if let image = pb.image, let png = image.pngData() {
            guard let fileId = await uploadImage(png) else { return "Échec de l'envoi de l'image." }
            return await postClip([
                "contentType": "image", "fileId": fileId, "fileType": "image/png", "targets": "all",
                "deviceName": cfg.deviceName, "deviceId": cfg.deviceId,
            ])
        }
        return "Presse-papiers vide."
    }

    private func postClip(_ body: [String: Any]) async -> String {
        guard let url = URL(string: cfg.httpURL + "/clip") else { return "URL invalide." }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(cfg.accountId, forHTTPHeaderField: "x-account-id")
        req.setValue(cfg.secret, forHTTPHeaderField: "x-token")
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)

        guard let (data, resp) = try? await URLSession.shared.data(for: req) else {
            return "Serveur injoignable."
        }
        if (resp as? HTTPURLResponse)?.statusCode == 401 { return "Secret incorrect." }
        let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        let delivered = (obj?["delivered"] as? Int) ?? 0
        return delivered > 0 ? "Envoyé à \(delivered) appareil(s)." : "Aucun PC en ligne."
    }

    private func uploadImage(_ data: Data) async -> String? {
        guard let url = URL(string: cfg.httpURL + "/files") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        req.setValue(cfg.accountId, forHTTPHeaderField: "x-account-id")
        req.setValue(cfg.secret, forHTTPHeaderField: "x-token")
        req.setValue("image/png", forHTTPHeaderField: "x-file-type")
        req.httpBody = data
        guard let (respData, resp) = try? await URLSession.shared.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let obj = try? JSONSerialization.jsonObject(with: respData) as? [String: Any] else { return nil }
        return obj["fileId"] as? String
    }
}
