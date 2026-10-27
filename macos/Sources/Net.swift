import Combine
import Foundation
import Network
import SwiftUI

// Состояние интернета на этом Mac и задержка до VPN-сервера (индикатор в боковой панели).
final class NetMonitor: ObservableObject {
    @Published private(set) var internetText = "проверка…"
    @Published private(set) var internetColor = Theme.dots
    @Published private(set) var serverText = "—"
    @Published private(set) var serverColor = Theme.dots

    private let store: Store
    private let vpn: VPNController
    private let channels: Channels
    private let path = NWPathMonitor()
    private var timer: Timer?
    private var checking = false
    private var serverIke: (host: String, result: ProbeResult)?
    private var cancellables = Set<AnyCancellable>()

    init(store: Store, vpn: VPNController, channels: Channels) {
        self.store = store
        self.vpn = vpn
        self.channels = channels
        // Сеть поменялась (сменился Wi-Fi, отключили кабель) — проверяем сразу.
        path.pathUpdateHandler = { [weak self] _ in DispatchQueue.main.async { self?.check() } }
        path.start(queue: DispatchQueue(label: "omnivpn.path"))
        let t = Timer(timeInterval: 10, repeats: true) { [weak self] _ in self?.check() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
        vpn.$phase.removeDuplicates().dropFirst().receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.check() }.store(in: &cancellables)
        store.$selectedID.removeDuplicates().dropFirst().receive(on: RunLoop.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.check() } }.store(in: &cancellables)
    }

    func check() {
        guard !checking else { return }
        checking = true
        let connected = vpn.phase == .connected
        let target = (connected ? vpn.active : nil) ?? store.selected
        let host = target?.server.trimmingCharacters(in: .whitespaces) ?? ""
        let cached = target.flatMap { channels.result($0) }
        let ike = serverIke

        DispatchQueue.global(qos: .utility).async { [weak self] in
            let inet = NetMonitor.internet()
            let dns = NetMonitor.dnsWorks()
            var srv: ProbeResult? = host.isEmpty ? nil : NetMonitor.ping(host)
            var newIke: (host: String, result: ProbeResult)?
            if !host.isEmpty, let s = srv, s.state != .up, !s.nameNotFound {
                // Сервер не отвечает на ping (FortiGate режет ICMP) — берём время ответа IPsec, не чаще раза в минуту.
                if !connected, let c = cached, c.state == .up, Date().timeIntervalSince(c.when) < 90 {
                    srv = c
                } else if let i = ike, i.host == host, Date().timeIntervalSince(i.result.when) < 60 {
                    srv = i.result
                } else if inet.state == .up {
                    let r = IkeProbe.checkSync(host)
                    srv = r
                    newIke = (host, r)
                }
            }
            DispatchQueue.main.async {
                guard let self = self else { return }
                if let n = newIke { self.serverIke = n }
                self.publish(inet: inet, dns: dns, server: srv)
                self.checking = false
            }
        }
    }

    private func publish(inet: ProbeResult, dns: Bool, server: ProbeResult?) {
        if inet.state != .up {
            internetText = "нет связи"; internetColor = Theme.err
        } else if !dns {
            internetText = "нет DNS"; internetColor = Theme.busy
        } else {
            internetText = "работает, \(inet.rttMs) мс"; internetColor = Theme.ok
        }
        if let s = server {
            if s.state == .up {
                serverText = "\(s.rttMs) мс"
                serverColor = s.rttMs < 100 ? Theme.ok : s.rttMs < 250 ? Theme.busy : Theme.err
            } else {
                serverText = s.nameNotFound ? "имя не найдено" : "нет ответа"
                serverColor = Theme.err
            }
        } else {
            serverText = "—"; serverColor = Theme.dots
        }
    }

    // Интернет есть, если открылось TCP-соединение хотя бы с одним из независимых узлов.
    static func internet() -> ProbeResult {
        let targets: [(String, UInt16)] = [("77.88.8.8", 53), ("1.1.1.1", 443), ("8.8.8.8", 443)]
        let queue = DispatchQueue(label: "omnivpn.inet")
        let done = DispatchSemaphore(value: 0)
        let start = Date()
        var rtt: Int?
        var failed = 0
        var signalled = false
        var connections: [NWConnection] = []
        for (host, port) in targets {
            let c = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
            connections.append(c)
            c.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if rtt == nil { rtt = Int(Date().timeIntervalSince(start) * 1000) }
                    if !signalled { signalled = true; done.signal() }
                case .failed, .waiting:
                    failed += 1
                    if failed == targets.count && !signalled { signalled = true; done.signal() }
                default:
                    break
                }
            }
            c.start(queue: queue)
        }
        _ = done.wait(timeout: .now() + 2.5)
        let result: ProbeResult = queue.sync {
            signalled = true
            return rtt.map { ProbeResult(state: .up, rttMs: $0) } ?? ProbeResult(state: .down)
        }
        connections.forEach { $0.cancel() }
        return result
    }

    static func dnsWorks() -> Bool {
        let done = DispatchSemaphore(value: 0)
        var ok = false
        DispatchQueue.global(qos: .utility).async {
            var hints = addrinfo()
            hints.ai_family = AF_INET
            var res: UnsafeMutablePointer<addrinfo>?
            if getaddrinfo("ya.ru", nil, &hints, &res) == 0 {
                ok = res != nil
                freeaddrinfo(res)
            }
            done.signal()
        }
        return done.wait(timeout: .now() + 2.5) == .success && ok
    }

    // Системный ping (без прав администратора): «time=12.3 ms».
    static func ping(_ host: String) -> ProbeResult {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/sbin/ping")
        p.arguments = ["-c", "1", "-t", "2", host]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = out
        do { try p.run() } catch { return ProbeResult(state: .down) }
        p.waitUntilExit()
        let text = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        if let r = text.range(of: #"time=([0-9.]+) ms"#, options: .regularExpression) {
            let value = text[r].dropFirst(5).dropLast(3)
            return ProbeResult(state: .up, rttMs: Int((Double(value) ?? 0).rounded()))
        }
        return ProbeResult(state: .down, nameNotFound: text.contains("cannot resolve") || text.contains("Unknown host"))
    }
}
