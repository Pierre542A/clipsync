import AppIntents

// App Intent : envoie le presse-papiers vers les PC.
// openAppWhenRun = false → s'exécute en arrière-plan (idéal pour le triple-tap au dos).
struct SendClipboardIntent: AppIntent {
    static var title: LocalizedStringResource = "Envoyer le presse-papiers vers mes PC"
    static var description = IntentDescription("Envoie le contenu copié (texte ou image) vers ClipSync.")
    static var openAppWhenRun: Bool = false

    func perform() async throws -> some IntentResult & ProvidesDialog {
        let cfg = Config.load()
        guard cfg.isConfigured else {
            return .result(dialog: "ClipSync n'est pas configuré — ouvre l'app et renseigne le serveur et le compte.")
        }
        let message = await ClipSender(cfg: cfg).sendPasteboard()
        return .result(dialog: IntentDialog(stringLiteral: message))
    }
}
