import AppIntents

// Expose l'action à Siri, au Centre de contrôle, aux widgets et au triple-tap au dos.
struct ClipSyncShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: SendClipboardIntent(),
            phrases: [
                "Envoie au PC avec \(.applicationName)",
                "\(.applicationName) envoie le presse-papiers",
            ],
            shortTitle: "Envoyer au PC",
            systemImageName: "paperplane.fill"
        )
    }
}
