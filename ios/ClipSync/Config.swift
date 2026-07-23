import Foundation
import UIKit

// Configuration locale (persistée dans UserDefaults).
// Le `secret` mériterait le Keychain — à migrer avec le chiffrement bout-en-bout.
struct Config: Codable {
    var serverURL: String = "wss://clip.lateliercbd.com/ws"
    var httpURL: String = "https://clip.lateliercbd.com"
    var accountId: String = ""
    var secret: String = ""
    var deviceId: String = ""
    var deviceName: String = ""

    private static let storeKey = "clipsync.config"

    static func load() -> Config {
        if let data = UserDefaults.standard.data(forKey: storeKey),
           var cfg = try? JSONDecoder().decode(Config.self, from: data) {
            cfg.ensureDefaults()
            return cfg
        }
        var cfg = Config()
        cfg.ensureDefaults()
        cfg.save()
        return cfg
    }

    mutating func ensureDefaults() {
        if deviceId.isEmpty { deviceId = "ios-" + String(UUID().uuidString.prefix(8)).lowercased() }
        if deviceName.isEmpty { deviceName = UIDevice.current.name }
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Config.storeKey)
        }
    }

    var isConfigured: Bool { !accountId.isEmpty && !secret.isEmpty }
}
