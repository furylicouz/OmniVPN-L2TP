import AppKit
import SwiftUI

// Снимки экранов для проверки дизайна (собирается отдельно, с -D SNAPSHOT).
let outDir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "."
let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let store = Store(load: false)
let vpn = VPNController()
let nav = Navigation()
let profiles = ProfileIO(store: store, vpn: vpn, nav: nav)
let channels = Channels(store: store, vpn: vpn)
let rdpCenter = RdpCenter(store: store, vpn: vpn, channels: channels, nav: nav)
let netMonitor = NetMonitor(store: store, vpn: vpn, channels: channels)
let updater = Updater()
channels.autoPrimaryPending = false
let office = VPNConnection(name: "Офис — основной", server: "vpn.example.ru", routes: "192.168.88.0/24", user: "user", savePassword: true, group: "Офис")
let reserve = VPNConnection(name: "Офис — резерв", server: "vpn2.example.ru", user: "user", group: "Офис")
let emergency = VPNConnection(name: "Офис — аварийный", server: "vpn3.example.ru", user: "user", group: "Офис")
channels.testSet("vpn.example.ru", ProbeResult(state: .up, rttMs: 24))
channels.testSet("vpn2.example.ru", ProbeResult(state: .up, rttMs: 31))
channels.testSet("vpn3.example.ru", ProbeResult(state: .down))
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 540), styleMask: [.borderless], backing: .buffered, defer: false)

func shot(_ name: String) {
    let host = NSHostingView(rootView: MainView().environmentObject(store).environmentObject(vpn).environmentObject(nav).environmentObject(profiles).environmentObject(channels).environmentObject(rdpCenter).environmentObject(netMonitor).environmentObject(updater))
    host.frame = NSRect(x: 0, y: 0, width: 820, height: 540)
    window.contentView = host
    host.layoutSubtreeIfNeeded()
    RunLoop.main.run(until: Date().addingTimeInterval(0.6))
    let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds)!
    host.cacheDisplay(in: host.bounds, to: rep)
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: outDir + "/" + name + ".png"))
    print("saved", name)
}

shot("0-empty")
nav.page = 1
shot("0-rdp-empty")
nav.page = 0
store.connections = [office, reserve, emergency]
store.selectedID = office.id
shot("1-idle")
vpn.testSet(.connecting, active: office, user: "user", stage: "Соединение с сервером vpn.example.ru (IPsec)…")
shot("2-connecting")
var rx: [Double] = [], tx: [Double] = []
for i in 0..<60 {
    rx.append(1.4e6 + 1.2e6 * sin(Double(i) / 6) + Double((i * 7919) % 400_000))
    tx.append(120e3 + 60e3 * cos(Double(i) / 4) + Double((i * 104729) % 30_000))
}
vpn.testSet(.connected, active: office, user: "user", ip: "10.0.17.25", duration: 5077,
            bytes: (1.84 * 1024 * 1024 * 1024, 212 * 1024 * 1024), speed: (2.31 * 1024 * 1024, 148 * 1024), history: (rx, tx))
shot("3-connected")
vpn.testSet(.idle, active: office, user: "user", fallback: true)
vpn.banner = Banner(text: "Сервер не отвечает по L2TP/IPsec: проверьте адрес сервера, общий ключ (PSK) и фаервол (UDP 500, 4500, 1701). (код 8/2)", isError: true)
shot("4-error")
nav.page = 2
shot("5-connections")
nav.page = 3
shot("6-about")
let nextRelease = UpdateInfo(version: "1.9.1", notes: "Исправлен двойной домен в логине RDP\nПроверка обновлений раз в 6 часов",
                             file: "OmniVPNConnect-1.9.1.dmg", sha256: "", size: 1_672_743)
updater.testSet(.idle, available: nextRelease, status: "Доступна версия 1.9.1 (у вас \(appVersion))", color: Theme.primary)
shot("6b-about-update")
updater.testSet(.downloading, available: nextRelease, progress: 0.45, status: "Загрузка версии 1.9.1: 45%  ·  0,7 МБ из 1,6 МБ", color: Theme.label)
shot("6c-about-download")
updater.testSet(.idle, available: nil, status: "Ещё не проверялись", color: Theme.label)
store.rdpHosts = [RdpHost(name: "Бухгалтерия", host: "192.168.20.25", user: "CORP\\user", vpn: "Офис"),
                  RdpHost(name: "Терминальный сервер", host: "broker.corp.local", user: "CORP\\user", collection: "Office", gateway: "gw.example.ru"),
                  RdpHost(name: "Домашний ПК", host: "127.0.0.1")]
nav.page = 1
shot("7-rdp")
nav.rdpEditID = store.rdpHosts[1].id
shot("8-rdp-edit")
