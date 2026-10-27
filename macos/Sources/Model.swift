import Foundation
import Security

// Одно VPN-подключение пользователя. Общий ключ и пароль лежат в связке ключей, а не в файле.
struct VPNConnection: Codable, Identifiable, Equatable {
    var id = UUID()
    var name = ""
    var server = ""
    var routes = ""
    var user = ""
    var savePassword = false
    // Группа каналов одного офиса (порядок в списке = приоритет). Optional — чтобы читались старые файлы без поля.
    var group: String?
    // "latency" — в группе подключать к каналу с наименьшей задержкой. Optional — для старых файлов.
    var balance: String?
    // Домен для RDP-компьютеров, которым нужен этот VPN (например CORP).
    var domain: String?
    // DNS VPN: серверы через запятую (не больше двух) и DNS-домен (corp.local).
    var dns: String?
    var dnsDomain: String?

    var dnsServers: [String] { (dns ?? "").components(separatedBy: CharacterSet(charactersIn: ", ;\t")).filter { !$0.isEmpty } }
    var dnsDomainName: String { (dnsDomain ?? "").trimmingCharacters(in: .whitespaces) }

    var groupName: String { (group ?? "").trimmingCharacters(in: .whitespaces) }
    var isLatency: Bool { balance == "latency" }
    var domainName: String { (domain ?? "").trimmingCharacters(in: .whitespaces) }
}

enum Keychain {
    static let service = "OmniVPN Connect"
    static let legacyService = "L2TP Connect"

    // Значения, сохранённые до переименования программы, переносятся при первом чтении.
    static func get(_ account: String) -> String? {
        if let value = read(account, service: service) { return value }
        guard let old = read(account, service: legacyService) else { return nil }
        set(old, for: account)
        return old
    }

    private static func read(_ account: String, service: String) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess, let data = out as? Data else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    static func set(_ value: String, for account: String) {
        delete(account)
        guard !value.isEmpty else { return }
        let item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrLabel as String: "OmniVPN Connect",
            kSecValueData as String: Data(value.utf8),
        ]
        SecItemAdd(item as CFDictionary, nil)
    }

    static func delete(_ account: String) {
        for s in [service, legacyService] {
            let query: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: s,
                kSecAttrAccount as String: account,
            ]
            SecItemDelete(query as CFDictionary)
        }
    }
}

final class Store: ObservableObject {
    private struct File: Codable {
        var connections: [VPNConnection]
        var selected: UUID?
        var rdp: [RdpHost]?
    }

    @Published var connections: [VPNConnection] = []
    @Published var rdpHosts: [RdpHost] = []
    @Published var selectedID: UUID? {
        didSet { if oldValue != selectedID { save() } }
    }

    static var folder: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("OmniVPNConnect", isDirectory: true)
    }

    // Папка настроек до переименования программы.
    private static var legacyFolder: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("L2TPConnect", isDirectory: true)
    }

    private var fileURL: URL { Store.folder.appendingPathComponent("connections.json") }
    var persist = true

    // load = false — пустой список без чтения диска (для тестового стенда).
    init(load: Bool = true) {
        persist = load
        guard load else { return }
        let fm = FileManager.default
        if !fm.fileExists(atPath: Store.folder.path) && fm.fileExists(atPath: Store.legacyFolder.path) {
            try? fm.moveItem(at: Store.legacyFolder, to: Store.folder)
        }
        if let data = try? Data(contentsOf: fileURL), let file = try? JSONDecoder().decode(File.self, from: data) {
            connections = file.connections
            rdpHosts = file.rdp ?? []
            selectedID = file.selected
        }
        if selected == nil { selectedID = connections.first?.id }
    }

    var selected: VPNConnection? {
        connections.first { $0.id == selectedID } ?? connections.first
    }

    func save() {
        guard persist else { return }
        do {
            try FileManager.default.createDirectory(at: Store.folder, withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(File(connections: connections, selected: selectedID, rdp: rdpHosts))
            try data.write(to: fileURL, options: .atomic)
        } catch {
            NSLog("OmniVPN Connect: save failed: \(error)")
        }
    }

    func upsert(_ c: VPNConnection) {
        if let i = connections.firstIndex(where: { $0.id == c.id }) {
            connections[i] = c
        } else {
            connections.append(c)
        }
        save()
    }

    func remove(_ id: UUID) {
        connections.removeAll { $0.id == id }
        Keychain.delete(id.uuidString + ".psk")
        Keychain.delete(id.uuidString + ".password")
        if selectedID == id { selectedID = connections.first?.id }
        save()
    }

    func upsertRdp(_ h: RdpHost) {
        if let i = rdpHosts.firstIndex(where: { $0.id == h.id }) {
            rdpHosts[i] = h
        } else {
            rdpHosts.append(h)
        }
        save()
    }

    func removeRdp(_ id: UUID) {
        rdpHosts.removeAll { $0.id == id }
        save()
    }

    func psk(_ id: UUID) -> String { Keychain.get(id.uuidString + ".psk") ?? "" }
    func setPsk(_ value: String, _ id: UUID) { Keychain.set(value, for: id.uuidString + ".psk") }
    func password(_ id: UUID) -> String { Keychain.get(id.uuidString + ".password") ?? "" }
    func setPassword(_ value: String, _ id: UUID) { Keychain.set(value, for: id.uuidString + ".password") }
}

enum RouteList {
    // «192.168.1.0/24, 10.0.0.5» -> ["192.168.1.0/24", "10.0.0.5/32"]; nil и плохой элемент при ошибке.
    static func parse(_ text: String) -> (routes: [String], bad: String?) {
        var result: [String] = []
        let parts = text.components(separatedBy: CharacterSet(charactersIn: ",; \t\r\n")).filter { !$0.isEmpty }
        for raw in parts {
            let pieces = raw.split(separator: "/", omittingEmptySubsequences: false)
            guard pieces.count <= 2 else { return ([], raw) }
            var prefix = 32
            if pieces.count == 2 {
                guard let p = Int(pieces[1]), (1...32).contains(p) else { return ([], raw) }
                prefix = p
            }
            let octets = pieces[0].split(separator: ".", omittingEmptySubsequences: false).map { UInt32($0) }
            guard octets.count == 4, octets.allSatisfy({ $0 != nil && $0! <= 255 }) else { return ([], raw) }
            var v = octets.reduce(UInt32(0)) { ($0 << 8) | $1! }
            v &= prefix == 32 ? 0xFFFF_FFFF : ~(0xFFFF_FFFF >> UInt32(prefix))
            let norm = "\(v >> 24).\((v >> 16) & 255).\((v >> 8) & 255).\(v & 255)/\(prefix)"
            if !result.contains(norm) { result.append(norm) }
        }
        return (result, nil)
    }
}

enum Fmt {
    static func bytes(_ value: Double) -> String {
        let units = ["Б", "КБ", "МБ", "ГБ", "ТБ"]
        var b = value
        var i = 0
        while b >= 1024 && i < units.count - 1 {
            b /= 1024
            i += 1
        }
        if i == 0 { return "\(Int(b)) Б" }
        let f = b < 10 ? "%.2f" : b < 100 ? "%.1f" : "%.0f"
        return String(format: f, b).replacingOccurrences(of: ".", with: ",") + " " + units[i]
    }

    static func speed(_ v: Double) -> String { bytes(v) + "/с" }

    static func duration(_ t: TimeInterval) -> String {
        let s = max(0, Int(t))
        let hms = String(format: "%02d:%02d:%02d", (s / 3600) % 24, (s / 60) % 60, s % 60)
        return s >= 86400 ? "\(s / 86400)д \(hms)" : hms
    }
}
