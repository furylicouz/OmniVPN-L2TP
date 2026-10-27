import Foundation

// Разбор и запись .omnivpn: пример, круговой прогон и ошибки (те же случаи, что в Windows-стенде).
let bundle = try! ProfileFile.read(URL(fileURLWithPath: CommandLine.arguments[1]))
for e in bundle.vpn {
    print("  [\(e.name)] server=\(e.server) psk=\(e.psk) routes=\(e.routes) group=\(e.group) balance=\(e.balance) domain=\(e.domain) dns=\(e.dns) dnsdomain=\(e.dnsDomain) user=\(e.user) password=\(e.password.isEmpty ? "-" : "set")")
}
for h in bundle.rdp {
    print("  ПК [\(h.name)] host=\(h.host) user=\(h.user) domain=\(h.domainName) vpn=\(h.vpn) collection=\(h.collectionName) gateway=\(h.gatewayName) проверка=\(h.probeHost):\(h.probePort)")
}
let conns = bundle.vpn.map { VPNConnection(name: $0.name, server: $0.server, routes: $0.routes, user: $0.user, group: $0.group.isEmpty ? nil : $0.group,
                                             balance: $0.balance.isEmpty ? nil : $0.balance, domain: $0.domain.isEmpty ? nil : $0.domain,
                                             dns: $0.dns.isEmpty ? nil : $0.dns, dnsDomain: $0.dnsDomain.isEmpty ? nil : $0.dnsDomain) }
let again = try! ProfileFile.parse(ProfileFile.write(Array(zip(conns, bundle.vpn.map { $0.psk })), rdp: bundle.rdp))
let sameVpn = again.vpn.count == bundle.vpn.count && zip(again.vpn, bundle.vpn).allSatisfy {
    $0.name == $1.name && $0.server == $1.server && $0.psk == $1.psk && $0.routes == $1.routes && $0.user == $1.user && $0.group == $1.group && $0.balance == $1.balance && $0.domain == $1.domain && $0.dns == $1.dns && $0.dnsDomain == $1.dnsDomain
}
let sameRdp = again.rdp.count == bundle.rdp.count && zip(again.rdp, bundle.rdp).allSatisfy {
    $0.name == $1.name && $0.host == $1.host && $0.user == $1.user && $0.vpn == $1.vpn && $0.collection == $1.collection && $0.gateway == $1.gateway && $0.domain == $1.domain && $0.fullScreen == $1.fullScreen && $0.multiMon == $1.multiMon
}
print("roundtrip:", sameVpn && sameRdp)
for bad in ["[A]\nserver = x", "[A]\npsk = k", "[A]\nserver=x\npsk=k\nroutes=10.0.0.0/40", "[A]\nserver=x\npsk=k\n[a]\nserver=y\npsk=k",
            "[P]\ntype=rdp", "[P]\ntype=rdp\nhost=pc:99999", "[P]\ntype=rdp\nhost=pc\ncollection=my coll", "[P]\ntype=rdp\nhost=pc\ngateway=gw:0", "[P]\ntype=rdp\nhost=pc\ndomain=CORP\\x", "[A]\nserver=x\npsk=k\ndomain=my domain", "[A]\nserver=x\npsk=k\ndns=10.0.0.1,10.0.0.2,10.0.0.3", "[A]\nserver=x\npsk=k\ndns=dns.local", "[A]\nserver=x\npsk=k\ndnsdomain=corp loc", "просто текст", ""] {
    do { _ = try ProfileFile.parse(bad); print("  NO ERROR for:", bad) } catch { print("  error ok:", error.localizedDescription) }
}
