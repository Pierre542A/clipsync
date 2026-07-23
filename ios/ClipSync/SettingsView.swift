import SwiftUI

struct SettingsView: View {
    @State var config: Config
    var onSave: (Config) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Serveur") {
                    TextField("wss://…/ws", text: $config.serverURL)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                    TextField("https://…", text: $config.httpURL)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                }

                Section {
                    TextField("Identifiant de compte", text: $config.accountId)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                    SecureField("Secret partagé", text: $config.secret)
                } header: {
                    Text("Compte")
                } footer: {
                    Text("Doivent être identiques sur tes PC. Le 1ᵉʳ appareil qui se connecte fixe le secret.")
                }

                Section("Cet appareil") {
                    TextField("Nom", text: $config.deviceName)
                    LabeledContent("ID", value: config.deviceId)
                }
            }
            .navigationTitle("Réglages")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Enregistrer") { onSave(config) }
                }
                ToolbarItem(placement: .cancellationAction) {
                    Button("Annuler") { dismiss() }
                }
            }
        }
    }
}
