import Foundation

// Выбор канала («меньше задержка» и «по порядку») и логин с доменом — те же случаи, что в Windows-стенде.
let store = Store(load: false)
let vpn = VPNController()
let nav = Navigation()
let channels = Channels(store: store, vpn: vpn)
let rdp = RdpCenter(store: store, vpn: vpn, channels: channels, nav: nav)
var main = VPNConnection(name: "Офис — основной", server: "a.test", group: "Офис", balance: "latency", domain: "CORP")
var res = VPNConnection(name: "Офис — резерв", server: "b.test", group: "Офис", balance: "latency")
var emer = VPNConnection(name: "Офис — аварийный", server: "c.test", group: "Офис", balance: "latency")
store.connections = [main, res, emer]
store.selectedID = main.id
channels.testSet("a.test", ProbeResult(state: .up, rttMs: 120))
channels.testSet("b.test", ProbeResult(state: .up, rttMs: 40))
channels.testSet("c.test", ProbeResult(state: .down))
print("  задержка: самый быстрый = \(channels.fastestUp(store.connections)?.name ?? "-")")
print("  задержка: после ошибки на резерве -> \(channels.next(after: res)?.name ?? "-")")
print("  задержка: переспрашивать про основной = \(channels.better(than: res) != nil)")
for i in store.connections.indices { store.connections[i].balance = nil }
main = store.connections[0]; res = store.connections[1]
print("  по порядку: после ошибки на основном -> \(channels.next(after: main)?.name ?? "-")")
print("  по порядку: переспрашивать при резерве -> \(channels.better(than: res)?.name ?? "нет")")
for u in ["user", "BRANCH\\user", "user@corp.local"] { print("  логин «\(u)» + домен CORP -> «\(RdpLauncher.fullUser(u, "CORP"))»") }
let pc = RdpHost(name: "Бухгалтерия", host: "192.168.20.25", user: "user", vpn: "Офис")
print("  домен компьютера из VPN «Офис»: \(rdp.domain(for: pc))")
