import SwiftUI

struct ContentView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        // Sous-vue qui observe directement le client (pour rafraîchir présence/état).
        HomeView(client: model.client)
            .environmentObject(model)
    }
}

private struct HomeView: View {
    @EnvironmentObject var model: AppModel
    @ObservedObject var client: RelayClient
    @State private var showSettings = false

    var onlineCount: Int { client.devices.filter { $0.online }.count }

    var body: some View {
        NavigationStack {
            List {
                Section("État") {
                    HStack(spacing: 8) {
                        Circle()
                            .fill(client.connected ? Color.green : Color.gray)
                            .frame(width: 10, height: 10)
                        Text(client.connected ? "Connecté" : "Hors ligne")
                        Spacer()
                        Text(client.lastEvent)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }

                Section("Envoyer") {
                    Button {
                        Task { await client.sendPasteboard() }
                    } label: {
                        Label("Envoyer le presse-papiers", systemImage: "paperplane.fill")
                    }
                    .disabled(!client.connected)
                    Text("Astuce : assigne « Envoyer au PC » au triple-tap au dos (Réglages → Accessibilité → Toucher → Toucher l'arrière).")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Section("Appareils (\(onlineCount) en ligne)") {
                    if client.devices.isEmpty {
                        Text("Aucun autre appareil").foregroundStyle(.secondary)
                    } else {
                        ForEach(client.devices) { device in
                            HStack {
                                Image(systemName: icon(device.platform))
                                    .frame(width: 24)
                                VStack(alignment: .leading) {
                                    Text(device.name)
                                    Text(device.platform).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Circle()
                                    .fill(device.online ? Color.green : Color.gray)
                                    .frame(width: 8, height: 8)
                            }
                        }
                    }
                }
            }
            .navigationTitle("ClipSync")
            .toolbar {
                Button { showSettings = true } label: { Image(systemName: "gearshape") }
            }
            .sheet(isPresented: $showSettings) {
                SettingsView(config: model.config) { newConfig in
                    model.save(newConfig)
                    showSettings = false
                }
            }
        }
    }

    private func icon(_ platform: String) -> String {
        switch platform {
        case "windows": return "pc"
        case "ios": return "iphone"
        case "mac": return "laptopcomputer"
        default: return "desktopcomputer"
        }
    }
}
