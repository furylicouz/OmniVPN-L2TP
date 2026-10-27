import AppKit
import Foundation
import SystemConfiguration

// Системная служба VPN «OmniVPN Connect» (то же, что L2TP через IPsec в «Настройки → VPN»).
// Создаётся только от root (через запрос пароля администратора), читается без прав.
enum SystemVPN {
    static let serviceName = "OmniVPN Connect"
    // Имя службы до переименования программы: удаляется при следующей настройке.
    static let legacyServiceNames = ["L2TP Connect"]
    static let routesFile = "/etc/ppp/omnivpnconnect.routes"
    static let legacyRoutesFile = "/etc/ppp/l2tpconnect.routes"
    static let ipUpFile = "/etc/ppp/ip-up"
    static let ipUpMarker = "# OmniVPN Connect"
    static let legacyIpUpMarker = "# L2TP Connect"

    struct Info {
        var serviceID: String
        var server: String
        var dnsServers: [String] = []
        var dnsDomain = ""
    }

    struct ApplyRequest: Codable {
        var server: String
        var routes: [String]
        var dns: [String]?
        var dnsDomain: String?
    }

    enum Failure: LocalizedError {
        case message(String)
        case cancelled
        var errorDescription: String? {
            switch self {
            case .message(let m): return m
            case .cancelled: return "Отменено"
            }
        }
    }

    private static func services(_ prefs: SCPreferences) -> [SCNetworkService] {
        guard let all = SCNetworkServiceCopyAll(prefs) else { return [] }
        return (all as NSArray).map { $0 as! SCNetworkService }
    }

    static func current() -> Info? {
        guard let prefs = SCPreferencesCreate(nil, "OmniVPNConnect" as CFString, nil) else { return nil }
        return info(prefs)
    }

    // Наша служба в указанных настройках (системных или, для проверки, во временном файле).
    static func info(_ prefs: SCPreferences) -> Info? {
        for s in services(prefs) where (SCNetworkServiceGetName(s) as String?) == serviceName {
            guard let id = SCNetworkServiceGetServiceID(s) as String? else { continue }
            var server = ""
            if let iface = SCNetworkServiceGetInterface(s),
               let cfg = SCNetworkInterfaceGetConfiguration(iface) as? [String: Any] {
                server = cfg[kSCPropNetPPPCommRemoteAddress as String] as? String ?? ""
            }
            var info = Info(serviceID: id, server: server)
            if let dns = SCNetworkServiceCopyProtocol(s, kSCNetworkProtocolTypeDNS),
               let cfg = SCNetworkProtocolGetConfiguration(dns) as? [String: Any] {
                info.dnsServers = cfg[kSCPropNetDNSServerAddresses as String] as? [String] ?? []
                info.dnsDomain = (cfg[kSCPropNetDNSSupplementalMatchDomains as String] as? [String])?.first ?? ""
            }
            return info
        }
        return nil
    }

    static func installedRoutes() -> [String] {
        guard let text = try? String(contentsOfFile: routesFile, encoding: .utf8) else { return [] }
        return text.split(separator: "\n").map { String($0) }.filter { !$0.isEmpty }
    }

    static var ipUpIsOurs: Bool {
        guard let text = try? String(contentsOfFile: ipUpFile, encoding: .utf8) else { return false }
        return text.contains(ipUpMarker) || text.contains(legacyIpUpMarker)
    }

    static var ipUpIsForeign: Bool {
        FileManager.default.fileExists(atPath: ipUpFile) && !ipUpIsOurs
    }

    static func needsApply(server: String, routes: [String], dns: [String], dnsDomain: String) -> Bool {
        guard let info = current(), info.server == server, info.dnsServers == dns,
              info.dnsDomain.lowercased() == dnsDomain.lowercased() else { return true }
        if routes.isEmpty { return !installedRoutes().isEmpty }
        return installedRoutes() != routes || (!ipUpIsOurs && !ipUpIsForeign)
    }

    // ---------- Выполняется от root (OmniVPN Connect --apply-config) ----------

    static func applyAsRoot(_ req: ApplyRequest) throws {
        guard let prefs = SCPreferencesCreate(nil, "OmniVPNConnect" as CFString, nil) else { throw scFailure("SCPreferencesCreate") }
        guard SCPreferencesLock(prefs, true) else { throw scFailure("SCPreferencesLock") }
        defer { SCPreferencesUnlock(prefs) }
        try configure(prefs, req)
        guard SCPreferencesCommitChanges(prefs) else { throw scFailure("SCPreferencesCommitChanges") }
        guard SCPreferencesApplyChanges(prefs) else { throw scFailure("SCPreferencesApplyChanges") }

        try writeRoutes(req.routes)
    }

    // Пересоздать нашу службу в настройках prefs (без сохранения).
    static func configure(_ prefs: SCPreferences, _ req: ApplyRequest) throws {
        // Константы вроде kSCNetworkInterfaceIPv4 заполняются при первом перечислении интерфейсов.
        _ = SCNetworkInterfaceCopyAll()
        for s in services(prefs) where isOurs(s) {
            SCNetworkServiceRemove(s)
        }

        guard let l2tp = SCNetworkInterfaceCreateWithInterface(kSCNetworkInterfaceIPv4, kSCNetworkInterfaceTypeL2TP),
              let ppp = SCNetworkInterfaceCreateWithInterface(l2tp, kSCNetworkInterfaceTypePPP),
              let service = SCNetworkServiceCreate(prefs, ppp) else { throw scFailure("SCNetworkServiceCreate") }
        SCNetworkServiceSetName(service, serviceName as CFString)
        guard SCNetworkServiceEstablishDefaultConfiguration(service),
              let top = SCNetworkServiceGetInterface(service) else { throw scFailure("EstablishDefaultConfiguration") }

        // Логин, пароль и общий ключ не храним в системе: они передаются при каждом подключении.
        let pppConfig: [String: Any] = [
            kSCPropNetPPPCommRemoteAddress as String: req.server,
            kSCPropNetPPPDisconnectOnLogout as String: 1,
        ]
        guard SCNetworkInterfaceSetConfiguration(top, pppConfig as CFDictionary) else { throw scFailure("PPP config") }

        let ipsec: [String: Any] = [
            kSCPropNetIPSecAuthenticationMethod as String: kSCValNetIPSecAuthenticationMethodSharedSecret as String,
            kSCPropNetIPSecLocalIdentifierType as String: kSCValNetIPSecLocalIdentifierTypeKeyID as String,
        ]
        guard SCNetworkInterfaceSetExtendedConfiguration(top, kSCEntNetIPSec, ipsec as CFDictionary) else { throw scFailure("IPSec config") }

        // OverridePrimary = 0: «Отправлять весь трафик через VPN» выключено (аналог снятой галки шлюза в Windows).
        if let ipv4 = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeIPv4) {
            let cfg: [String: Any] = [
                kSCPropNetIPv4ConfigMethod as String: kSCValNetIPv4ConfigMethodPPP as String,
                kSCPropNetOverridePrimary as String: 0,
            ]
            guard SCNetworkProtocolSetConfiguration(ipv4, cfg as CFDictionary) else { throw scFailure("IPv4 config") }
        }

        // DNS: свои серверы и домен. SupplementalMatchDomains — на DNS VPN уходят только имена этого домена
        // (при выключенном «весь трафик через VPN» иначе macOS не спрашивает DNS VPN вовсе).
        let dns = req.dns ?? []
        let domain = req.dnsDomain ?? ""
        if !dns.isEmpty || !domain.isEmpty {
            if SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeDNS) == nil {
                SCNetworkServiceAddProtocolType(service, kSCNetworkProtocolTypeDNS)
            }
            if let proto = SCNetworkServiceCopyProtocol(service, kSCNetworkProtocolTypeDNS) {
                var cfg: [String: Any] = [:]
                if !dns.isEmpty { cfg[kSCPropNetDNSServerAddresses as String] = dns }
                if !domain.isEmpty {
                    cfg[kSCPropNetDNSDomainName as String] = domain
                    cfg[kSCPropNetDNSSearchDomains as String] = [domain]
                    cfg[kSCPropNetDNSSupplementalMatchDomains as String] = [domain]
                }
                guard SCNetworkProtocolSetConfiguration(proto, cfg as CFDictionary) else { throw scFailure("DNS config") }
            }
        }

        guard let set = SCNetworkSetCopyCurrent(prefs), SCNetworkSetAddService(set, service) else { throw scFailure("SCNetworkSetAddService") }
    }

    // macOS не умеет хранить маршруты в настройках VPN, поэтому их ставит /etc/ppp/ip-up
    // (pppd запускает его от root при каждом подключении; маршруты исчезают вместе с интерфейсом).
    private static func writeRoutes(_ routes: [String]) throws {
        let fm = FileManager.default
        try? fm.removeItem(atPath: legacyRoutesFile)
        if routes.isEmpty {
            try? fm.removeItem(atPath: routesFile)
            return
        }
        try (routes.joined(separator: "\n") + "\n").write(toFile: routesFile, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o644, .ownerAccountID: 0], ofItemAtPath: routesFile)

        if ipUpIsForeign { return }
        let script = """
        #!/bin/sh
        \(ipUpMarker): маршруты «Сети за VPN». Аргументы pppd: $1 = интерфейс (pppN).
        [ -f \(routesFile) ] || exit 0
        while read -r net; do
          case "$net" in ""|*[!0-9./]*) continue ;; esac
          /sbin/route -n add -net "$net" -interface "$1" >/dev/null 2>&1
        done < \(routesFile)
        exit 0

        """
        try script.write(toFile: ipUpFile, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o755, .ownerAccountID: 0], ofItemAtPath: ipUpFile)
    }

    static func removeAsRoot() throws {
        if let prefs = SCPreferencesCreate(nil, "OmniVPNConnect" as CFString, nil), SCPreferencesLock(prefs, true) {
            for s in services(prefs) where isOurs(s) {
                SCNetworkServiceRemove(s)
            }
            SCPreferencesCommitChanges(prefs)
            SCPreferencesApplyChanges(prefs)
            SCPreferencesUnlock(prefs)
        }
        try? FileManager.default.removeItem(atPath: routesFile)
        try? FileManager.default.removeItem(atPath: legacyRoutesFile)
        if ipUpIsOurs { try? FileManager.default.removeItem(atPath: ipUpFile) }
    }

    private static func isOurs(_ s: SCNetworkService) -> Bool {
        guard let name = SCNetworkServiceGetName(s) as String? else { return false }
        return name == serviceName || legacyServiceNames.contains(name)
    }

    private static func scFailure(_ step: String) -> Failure {
        let code = SCError()
        return .message("\(step): \(String(cString: SCErrorString(code))) (\(code))")
    }

    // ---------- Запуск от администратора ----------

    // Запускает этот же исполняемый файл от root через стандартный запрос пароля macOS.
    static func runPrivileged(_ args: [String], prompt: String) throws {
        guard let exe = Bundle.main.executablePath else { throw Failure.message("Не найден исполняемый файл") }
        let shell = ([exe] + args).map { "'" + $0.replacingOccurrences(of: "'", with: "'\\''") + "'" }.joined(separator: " ")
        func quoted(_ s: String) -> String {
            "\"" + s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
        }
        let source = "do shell script \(quoted(shell + " 2>&1")) with prompt \(quoted(prompt)) with administrator privileges"
        var error: NSDictionary?
        guard let script = NSAppleScript(source: source) else { throw Failure.message("Не удалось запросить права администратора") }
        script.executeAndReturnError(&error)
        if let error = error {
            if (error[NSAppleScript.errorNumber] as? Int) == -128 { throw Failure.cancelled }
            throw Failure.message(error[NSAppleScript.errorMessage] as? String ?? "Ошибка выполнения с правами администратора")
        }
    }

    static func apply(server: String, routes: [String], dns: [String], dnsDomain: String) throws {
        let data = try JSONEncoder().encode(ApplyRequest(server: server, routes: routes, dns: dns, dnsDomain: dnsDomain))
        try runPrivileged(["--apply-config", data.base64EncodedString()],
                          prompt: "OmniVPN Connect настраивает VPN-подключение L2TP/IPsec в системе.")
    }

    static func remove() throws {
        try runPrivileged(["--remove-config"], prompt: "OmniVPN Connect удаляет своё VPN-подключение из системы.")
    }
}

// Режим командной строки для root-операций.
enum CLI {
    static func run(_ args: [String]) -> Int32? {
        guard args.count >= 2 else { return nil }
        switch args[1] {
        case "--apply-config":
            guard args.count >= 3, let data = Data(base64Encoded: args[2]),
                  let req = try? JSONDecoder().decode(SystemVPN.ApplyRequest.self, from: data) else {
                return fail("Неверные параметры")
            }
            guard geteuid() == 0 else { return fail("Нужны права root") }
            let server = req.server.trimmingCharacters(in: .whitespaces)
            guard !server.isEmpty, server.count < 256,
                  server.rangeOfCharacter(from: CharacterSet(charactersIn: " \t\r\n'\"`$;|&<>")) == nil else {
                return fail("Неверный адрес сервера")
            }
            let parsed = RouteList.parse(req.routes.joined(separator: ","))
            guard parsed.bad == nil else { return fail("Неверная сеть: \(parsed.bad!)") }
            let dns = ProfileFile.parseDns((req.dns ?? []).joined(separator: ","))
            if let error = dns.error ?? ProfileFile.checkDnsDomain(req.dnsDomain ?? "") { return fail(error) }
            do {
                try SystemVPN.applyAsRoot(SystemVPN.ApplyRequest(server: server, routes: parsed.routes,
                                                                 dns: dns.servers, dnsDomain: req.dnsDomain ?? ""))
                return 0
            } catch {
                return fail(error.localizedDescription)
            }
        case "--install-update":
            // Обновление программы, лежащей там, куда без прав администратора не записать.
            guard geteuid() == 0 else { return fail("Нужны права root") }
            guard args.count >= 4, args[2].hasSuffix(".app"), args[3].hasSuffix(".app") else { return fail("Неверные параметры") }
            do {
                try Updater.replace(args[2], with: args[3])
                return 0
            } catch {
                return fail(error.localizedDescription)
            }
        case "--remove-config":
            guard geteuid() == 0 else { return fail("Нужны права root") }
            do {
                try SystemVPN.removeAsRoot()
                return 0
            } catch {
                return fail(error.localizedDescription)
            }
        default:
            return nil
        }
    }

    private static func fail(_ message: String) -> Int32 {
        FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
        return 1
    }
}
