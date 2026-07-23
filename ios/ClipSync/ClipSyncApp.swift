import SwiftUI

@main
struct ClipSyncApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(model)
        }
    }
}

// État partagé de l'app : config + client réseau (WebSocket).
@MainActor
final class AppModel: ObservableObject {
    @Published var config: Config
    let client: RelayClient

    init() {
        let cfg = Config.load()
        self.config = cfg
        self.client = RelayClient(cfg: cfg)
        client.start()
    }

    func save(_ newConfig: Config) {
        config = newConfig
        newConfig.save()
        client.update(cfg: newConfig)
    }
}
