import Foundation
import SystemConfiguration

// Настройка службы VPN с DNS во ВРЕМЕННОМ файле настроек (не системном) — проверка без прав администратора.
setvbuf(stdout, nil, _IONBF, 0)
func step(_ m: String) { FileHandle.standardError.write(("  [шаг] " + m + "\n").data(using: .utf8)!) }
let path = NSTemporaryDirectory() + "omnivpn-dns-test.plist"
try? FileManager.default.removeItem(atPath: path)
step("prefs")
let prefs = SCPreferencesCreate(nil, "OmniVPNTest" as CFString, path as CFString)!
step("set")
let set = SCNetworkSetCreate(prefs)!
step("set ok")
SCNetworkSetSetName(set, "Test" as CFString)
SCNetworkSetSetCurrent(set)

func show(_ title: String) {
    let i = SystemVPN.info(prefs)
    print("  == \(title): сервер=\(i?.server ?? "-") DNS=\(i?.dnsServers ?? []) домен=\(i?.dnsDomain ?? "")")
}
do {
    step("configure")
    try SystemVPN.configure(prefs, .init(server: "vpn.example.ru", routes: [], dns: ["192.168.10.11", "192.168.10.12"], dnsDomain: "corp.local"))
    step("configure ok")
    show("с DNS и доменом")
    if let i = SystemVPN.info(prefs), let svc = SCNetworkServiceCopy(prefs, i.serviceID as CFString),
       let dns = SCNetworkServiceCopyProtocol(svc, kSCNetworkProtocolTypeDNS) {
        print("     конфигурация DNS:", SCNetworkProtocolGetConfiguration(dns) as? [String: Any] ?? [:])
    }
    try SystemVPN.configure(prefs, .init(server: "vpn.example.ru", routes: [], dns: [], dnsDomain: ""))
    show("без DNS")
    print("  служб «OmniVPN Connect» в наборе:", (SCNetworkServiceCopyAll(prefs) as? [SCNetworkService] ?? []).filter { (SCNetworkServiceGetName($0) as String?) == SystemVPN.serviceName }.count)
} catch {
    print("  ОШИБКА:", error.localizedDescription)
}
try? FileManager.default.removeItem(atPath: path)
