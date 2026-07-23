import Foundation
import CryptoKit

// Chiffrement de bout en bout — miroir exact du client Windows (.NET).
//  - authToken = HKDF(secret, "auth") → envoyé au serveur (auth uniquement).
//  - encKey    = HKDF(secret, "enc")  → jamais envoyé ; AES-256-GCM.
// Format du blob : nonce(12) ‖ ciphertext ‖ tag(16)  (= AES.GCM combined).
enum ClipCrypto {
    private static let salt = Data("clipsync-v1".utf8)

    static func encKey(_ secret: String) -> SymmetricKey {
        HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: Data(secret.utf8)),
            salt: salt,
            info: Data("clipsync-enc-v1".utf8),
            outputByteCount: 32)
    }

    static func authToken(_ secret: String) -> String {
        let key = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: Data(secret.utf8)),
            salt: salt,
            info: Data("clipsync-auth-v1".utf8),
            outputByteCount: 32)
        return key.withUnsafeBytes { Data($0) }.map { String(format: "%02x", $0) }.joined()
    }

    static func encrypt(_ plaintext: Data, key: SymmetricKey) -> Data? {
        (try? AES.GCM.seal(plaintext, using: key))?.combined
    }

    static func decrypt(_ blob: Data, key: SymmetricKey) -> Data? {
        guard let box = try? AES.GCM.SealedBox(combined: blob) else { return nil }
        return try? AES.GCM.open(box, using: key)
    }

    static func encryptText(_ text: String, key: SymmetricKey) -> String? {
        encrypt(Data(text.utf8), key: key)?.base64EncodedString()
    }

    static func decryptText(_ base64: String, key: SymmetricKey) -> String? {
        guard let blob = Data(base64Encoded: base64), let pt = decrypt(blob, key: key) else { return nil }
        return String(data: pt, encoding: .utf8)
    }
}
