import Foundation

// Appareil tel que renvoyé par le serveur (message `devices` / `welcome`).
struct DeviceInfo: Codable, Identifiable, Hashable {
    let deviceId: String
    let name: String
    let platform: String
    let online: Bool
    let lastSeen: Double?

    var id: String { deviceId }
}

// Provenance d'un clip reçu.
struct ClipFrom: Codable, Hashable {
    let deviceId: String?
    let name: String?
}
