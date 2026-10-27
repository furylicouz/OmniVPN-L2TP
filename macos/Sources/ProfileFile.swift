import AppKit
import Foundation
import UniformTypeIdentifiers

// Подключение из файла .omnivpn (вместе с секретами, которые в программе хранятся в связке ключей).
struct ProfileEntry {
    var name = ""
    var server = ""
    var psk = ""
    var routes = ""
    var group = ""
    var balance = ""
    var domain = ""
    var dns = ""
    var dnsDomain = ""
    var user = ""
    var password = ""
}

struct ProfileBundle {
    var vpn: [ProfileEntry] = []
    var rdp: [RdpHost] = []
}

// Файл профилей .omnivpn — тот же формат, что в Windows-версии:
//   [Офис]
//   server = vpn.example.ru
//   psk = ключ
//   routes = 192.168.88.0/24, 10.0.0.0/16   (необязательно)
//   group = Офис                            (необязательно: каналы одного офиса, порядок в файле = приоритет)
//   balance = задержка                      (необязательно: в группе подключать туда, где меньше задержка)
//   domain = CORP                           (необязательно: домен для RDP-компьютеров этого VPN)
//   dns = 192.168.10.11, 192.168.10.12      (необязательно: DNS-серверы VPN, не больше двух)
//   dnsdomain = corp.local                  (необязательно: DNS-домен, имена в нём ищутся через DNS VPN)
//   user = user                             (необязательно)
//   password = ...                          (необязательно, при экспорте не пишется)
//
//   [Бухгалтерия]
//   type = rdp
//   host = 192.168.20.25                    (можно с портом: 192.168.20.25:3390)
//   user = CORP\user                        (необязательно)
//   vpn = Офис                              (необязательно: группа или название VPN, без которого ПК недоступен)
//   domain = CORP                           (необязательно: домен для логина, иначе — домен VPN)
//   collection = Office                     (необязательно: alias коллекции RDS, тогда host — брокер)
//   gateway = gw.example.ru                 (необязательно: шлюз RD Gateway)
//   fullscreen = да | нет                   (по умолчанию да)
//   multimon = да | нет                     (по умолчанию нет)
enum ProfileFile {
    static let fileExtension = "omnivpn"
    static let type = UTType(exportedAs: "app.omnivpn.profile", conformingTo: .utf8PlainText)
    private static let maxSize = 256 * 1024

    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    private struct Section {
        var name = ""
        var named = false
        var values: [String: String] = [:]

        func get(_ keys: String...) -> String {
            for k in keys { if let v = values[k] { return v } }
            return ""
        }
    }

    static func read(_ url: URL) throws -> ProfileBundle {
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
        if size > maxSize { throw Failure(message: "Файл слишком большой для файла профилей.") }
        guard let data = try? Data(contentsOf: url) else { throw Failure(message: "Не удалось открыть файл.") }
        guard let text = String(data: data, encoding: .utf8) else { throw Failure(message: "Файл не в кодировке UTF-8.") }
        return try parse(text)
    }

    static func parse(_ raw: String) throws -> ProfileBundle {
        var sections: [Section] = []
        let text = raw.hasPrefix("\u{FEFF}") ? String(raw.dropFirst()) : raw
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")

        for (i, rawLine) in lines.enumerated() {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix(";") { continue }

            if line.hasPrefix("[") {
                guard let end = line.lastIndex(of: "]"), end > line.startIndex else {
                    throw lineError(i, "пустое название в [ ]")
                }
                let name = line[line.index(after: line.startIndex)..<end].trimmingCharacters(in: .whitespaces)
                if name.isEmpty { throw lineError(i, "пустое название в [ ]") }
                sections.append(Section(name: name, named: true))
                continue
            }

            guard let eq = line.firstIndex(of: "="), eq != line.startIndex else {
                throw lineError(i, "ожидалось «параметр = значение»")
            }
            // Параметры без секции — одно подключение, название из name или адреса.
            if sections.isEmpty { sections.append(Section()) }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            sections[sections.count - 1].values[key] = unquote(line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces))
        }

        var bundle = ProfileBundle()
        var vpnNames = Set<String>(), rdpNames = Set<String>()
        for var s in sections {
            if !s.named { s.name = s.get("name") }
            if s.get("type").lowercased() == "rdp" {
                let h = try toRdp(s)
                if !rdpNames.insert(h.name.lowercased()).inserted {
                    throw Failure(message: "Компьютер «\(h.name)»: встречается в файле дважды.")
                }
                bundle.rdp.append(h)
            } else {
                let e = try toVpn(s)
                if !vpnNames.insert(e.name.lowercased()).inserted {
                    throw Failure(message: "Подключение «\(e.name)»: встречается в файле дважды.")
                }
                bundle.vpn.append(e)
            }
        }
        if bundle.vpn.isEmpty && bundle.rdp.isEmpty { throw Failure(message: "В файле нет ни одного подключения.") }
        return bundle
    }

    private static func toVpn(_ s: Section) throws -> ProfileEntry {
        var e = ProfileEntry()
        e.server = s.get("server", "gateway", "host")
        e.name = s.name.isEmpty ? e.server : s.name
        e.psk = s.get("psk", "secret", "sharedsecret")
        e.routes = s.get("routes", "networks")
        e.group = s.get("group")
        e.balance = balance(s.get("balance", "mode"))
        e.domain = s.get("domain")
        e.dns = s.get("dns", "dnsservers")
        e.dnsDomain = s.get("dnsdomain", "dnssuffix", "searchdomain")
        e.user = s.get("user", "login", "username")
        e.password = s.get("password")
        let prefix = "Подключение «\(e.name)»: "
        if e.server.isEmpty { throw Failure(message: prefix + "не указан server.") }
        if e.server.rangeOfCharacter(from: CharacterSet(charactersIn: " \t'\"`$;|&<>")) != nil {
            throw Failure(message: prefix + "недопустимые символы в адресе сервера.")
        }
        if e.psk.isEmpty { throw Failure(message: prefix + "не указан psk (общий ключ).") }
        let parsed = RouteList.parse(e.routes)
        if let bad = parsed.bad { throw Failure(message: prefix + "неверная сеть «\(bad)».") }
        e.routes = parsed.routes.joined(separator: ", ")
        if let error = checkDomain(e.domain) { throw Failure(message: prefix + error) }
        let dns = parseDns(e.dns)
        if let error = dns.error ?? checkDnsDomain(e.dnsDomain) { throw Failure(message: prefix + error) }
        e.dns = dns.servers.joined(separator: ", ")
        return e
    }

    private static func toRdp(_ s: Section) throws -> RdpHost {
        var h = RdpHost()
        h.host = s.get("host", "address", "server")
        h.name = s.name.isEmpty ? h.host : s.name
        h.user = s.get("user", "login", "username")
        h.vpn = s.get("vpn")
        let coll = s.get("collection"), gw = s.get("gateway"), dom = s.get("domain")
        h.domain = dom.isEmpty ? nil : dom
        h.collection = coll.isEmpty ? nil : coll
        h.gateway = gw.isEmpty ? nil : gw
        h.fullScreen = flag(s.get("fullscreen"), true)
        h.multiMon = flag(s.get("multimon"), false)
        if let error = RdpHost.checkAddress(h.host) ?? (gw.isEmpty ? nil : RdpHost.checkAddress(gw)) ?? RdpHost.checkCollection(coll) ?? checkDomain(dom) {
            throw Failure(message: "Компьютер «\(h.name)»: \(error)")
        }
        return h
    }

    static func write(_ items: [(VPNConnection, String)], rdp: [RdpHost] = []) -> String {
        var s = "# OmniVPN Connect — профили VPN-подключений (L2TP/IPsec) и компьютеров (RDP).\r\n"
        s += "# Откройте файл двойным щелчком, чтобы добавить их в программу.\r\n"
        s += "# Внимание: файл содержит общий ключ (PSK), передавайте его только тем, кому он нужен.\r\n"
        for (c, psk) in items {
            s += "\r\n[\(c.name)]\r\n"
            s += "server = \(quote(c.server))\r\n"
            s += "psk = \(quote(psk))\r\n"
            if !c.routes.isEmpty { s += "routes = \(quote(c.routes))\r\n" }
            if !c.groupName.isEmpty { s += "group = \(quote(c.groupName))\r\n" }
            if c.isLatency { s += "balance = задержка\r\n" }
            if !c.domainName.isEmpty { s += "domain = \(quote(c.domainName))\r\n" }
            if !c.dnsServers.isEmpty { s += "dns = \(c.dnsServers.joined(separator: ", "))\r\n" }
            if !c.dnsDomainName.isEmpty { s += "dnsdomain = \(quote(c.dnsDomainName))\r\n" }
            if !c.user.isEmpty { s += "user = \(quote(c.user))\r\n" }
        }
        for h in rdp {
            s += "\r\n[\(h.name)]\r\ntype = rdp\r\n"
            s += "host = \(quote(h.host))\r\n"
            if !h.user.isEmpty { s += "user = \(quote(h.user))\r\n" }
            if !h.domainName.isEmpty { s += "domain = \(quote(h.domainName))\r\n" }
            if !h.vpn.isEmpty { s += "vpn = \(quote(h.vpn))\r\n" }
            if !h.collectionName.isEmpty { s += "collection = \(quote(h.collectionName))\r\n" }
            if !h.gatewayName.isEmpty { s += "gateway = \(quote(h.gatewayName))\r\n" }
            s += "fullscreen = \(h.fullScreen ? "да" : "нет")\r\n"
            if h.multiMon { s += "multimon = да\r\n" }
        }
        return s
    }

    // «задержка», «latency», «ping» — балансировка по задержке; всё остальное — по порядку.
    private static func balance(_ v: String) -> String {
        ["задержка", "latency", "ping", "fastest", "быстрый"].contains(v.trimmingCharacters(in: .whitespaces).lowercased()) ? "latency" : ""
    }

    // nil — домен в порядке. Домен — короткое имя (CORP) или DNS-имя (corp.local), без «\» и пробелов.
    static func checkDomain(_ d: String) -> String? {
        if d.isEmpty { return nil }
        let ok = d.allSatisfy { $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" || $0 == "_" }
        return ok ? nil : "домен указывается без «\\» и пробелов, например CORP."
    }

    // DNS-серверы: IPv4 через запятую или пробел, не больше двух (столько хранит Windows-версия).
    static func parseDns(_ text: String) -> (servers: [String], error: String?) {
        var servers: [String] = []
        for raw in text.components(separatedBy: CharacterSet(charactersIn: ",; \t")) where !raw.isEmpty {
            let parts = raw.split(separator: ".", omittingEmptySubsequences: false)
            guard parts.count == 4, parts.allSatisfy({ UInt8($0) != nil }) else {
                return ([], "неверный DNS-сервер «\(raw)» (нужен IPv4-адрес, например 192.168.10.11).")
            }
            if !servers.contains(raw) { servers.append(raw) }
        }
        if servers.count > 2 { return ([], "DNS-серверов можно указать не больше двух.") }
        return (servers, nil)
    }

    // DNS-домен: corp.local — буквы, цифры, точки и дефисы.
    static func checkDnsDomain(_ d: String) -> String? {
        if d.isEmpty { return nil }
        let ok = d.allSatisfy { $0.isLetter || $0.isNumber || $0 == "." || $0 == "-" }
        return ok ? nil : "DNS-домен указывается как corp.local, без пробелов и «\\»."
    }

    private static func flag(_ v: String, _ byDefault: Bool) -> Bool {
        switch v.trimmingCharacters(in: .whitespaces).lowercased() {
        case "да", "yes", "true", "1", "on": return true
        case "нет", "no", "false", "0", "off": return false
        default: return byDefault
        }
    }

    // Кавычки нужны, только если значение начинается или кончается пробелом либо само начинается с кавычки.
    private static func quote(_ v: String) -> String {
        guard let first = v.first, let last = v.last else { return v }
        if first.isWhitespace || last.isWhitespace || first == "\"" {
            return "\"" + v.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        return v
    }

    private static func unquote(_ v: String) -> String {
        if v.count >= 2, v.hasPrefix("\""), v.hasSuffix("\"") {
            return String(v.dropFirst().dropLast()).replacingOccurrences(of: "\\\"", with: "\"").replacingOccurrences(of: "\\\\", with: "\\")
        }
        return v
    }

    private static func lineError(_ index: Int, _ message: String) -> Failure {
        Failure(message: "Строка \(index + 1): \(message).")
    }
}

// Импорт и экспорт файлов профилей из окна программы.
final class ProfileIO: ObservableObject {
    private let store: Store
    private let vpn: VPNController
    private let nav: Navigation

    init(store: Store, vpn: VPNController, nav: Navigation) {
        self.store = store
        self.vpn = vpn
        self.nav = nav
    }

    func importWithPanel() {
        let panel = NSOpenPanel()
        panel.title = "Импорт профилей OmniVPN Connect"
        panel.allowedContentTypes = [ProfileFile.type]
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { importFile(url) }
    }

    func importFile(_ url: URL) {
        let bundle: ProfileBundle
        do {
            bundle = try ProfileFile.read(url)
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Не удалось прочитать файл профилей «\(url.lastPathComponent)»."
            alert.informativeText = error.localizedDescription
            alert.runModal()
            return
        }

        var details = ""
        for e in bundle.vpn {
            details += "•  \(e.name) — \(e.server)"
            if !e.routes.isEmpty { details += ", сети: \(e.routes)" }
            if existing(e.name) != nil { details += "  (заменит существующее)" }
            details += "\n"
        }
        for h in bundle.rdp {
            details += "•  ПК «\(h.name)» — \(h.host)"
            if store.rdpHosts.contains(where: { $0.name.lowercased() == h.name.lowercased() }) { details += "  (заменит существующий)" }
            details += "\n"
        }
        let alert = NSAlert()
        alert.messageText = "Добавить из файла «\(url.lastPathComponent)»?"
        alert.informativeText = details
        alert.addButton(withTitle: "Импортировать")
        alert.addButton(withTitle: "Отмена")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        var firstID: UUID?
        for e in bundle.vpn {
            var c = existing(e.name) ?? VPNConnection()
            c.name = e.name
            c.server = e.server
            c.routes = e.routes
            if !e.group.isEmpty { c.group = e.group }
            c.balance = e.balance.isEmpty ? nil : e.balance
            if !e.domain.isEmpty { c.domain = e.domain }
            if !e.dns.isEmpty { c.dns = e.dns }
            if !e.dnsDomain.isEmpty { c.dnsDomain = e.dnsDomain }
            if !e.user.isEmpty { c.user = e.user }
            if !e.password.isEmpty {
                c.savePassword = true
                store.setPassword(e.password, c.id)
            }
            store.upsert(c)
            store.setPsk(e.psk, c.id)
            if firstID == nil { firstID = c.id }
        }
        for var h in bundle.rdp {
            if let old = store.rdpHosts.first(where: { $0.name.lowercased() == h.name.lowercased() }) { h.id = old.id }
            store.upsertRdp(h)
        }
        if let id = firstID { store.selectedID = id }

        var parts: [String] = []
        if !bundle.vpn.isEmpty { parts.append("подключений: \(bundle.vpn.count)") }
        if !bundle.rdp.isEmpty { parts.append("компьютеров: \(bundle.rdp.count)") }
        let banner = Banner(text: "Импортировано " + parts.joined(separator: ", ") + ".", isError: false)
        if bundle.vpn.isEmpty {
            nav.rdpBanner = banner
            nav.page = 1
        } else {
            vpn.banner = banner
            nav.page = 0
        }
    }

    func export(_ list: [VPNConnection], rdp: [RdpHost] = [], baseName: String) -> Banner? {
        let panel = NSSavePanel()
        panel.title = "Экспорт профилей OmniVPN Connect"
        panel.allowedContentTypes = [ProfileFile.type]
        let safe = baseName.components(separatedBy: CharacterSet(charactersIn: "/:\\")).joined(separator: "_")
        panel.nameFieldStringValue = safe + "." + ProfileFile.fileExtension
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        let text = ProfileFile.write(list.map { ($0, store.psk($0.id)) }, rdp: rdp)
        do {
            try text.write(to: url, atomically: true, encoding: .utf8)
            return Banner(text: "Сохранено в «\(url.lastPathComponent)». Пароли в файл не записываются.", isError: false)
        } catch {
            return Banner(text: "Не удалось сохранить файл: \(error.localizedDescription)", isError: true)
        }
    }

    private func existing(_ name: String) -> VPNConnection? {
        store.connections.first { $0.name.lowercased() == name.lowercased() }
    }
}
