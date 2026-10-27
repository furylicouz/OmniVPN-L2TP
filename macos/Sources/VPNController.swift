import Foundation
import SystemConfiguration

enum Phase { case idle, connecting, connected, disconnecting }

struct Banner: Equatable {
    var text: String
    var isError: Bool
}

// Управление системной службой VPN через SCNetworkConnection: старт, стоп, статус, статистика.
final class VPNController: ObservableObject {
    @Published private(set) var phase: Phase = .idle
    @Published var stage = ""
    @Published var banner: Banner?
    @Published private(set) var bytesIn: Double = 0
    @Published private(set) var bytesOut: Double = 0
    @Published private(set) var speedIn: Double = 0
    @Published private(set) var speedOut: Double = 0
    @Published private(set) var duration: TimeInterval = 0
    @Published private(set) var ipAddress: String?
    @Published private(set) var historyIn = [Double](repeating: 0, count: 60)
    @Published private(set) var historyOut = [Double](repeating: 0, count: 60)
    @Published private(set) var active: VPNConnection?
    @Published private(set) var activeUser = ""
    // Последняя ошибка подключения из-за канала (не из-за логина/пароля) — можно предложить резерв.
    @Published private(set) var fallbackAllowed = false

    private var connection: SCNetworkConnection?
    private var serviceID: String?
    private var timer: Timer?
    private var connectedAt: Date?
    private var userStopped = false
    private var lastSample: (date: Date, inB: Double, outB: Double)?

    init() {
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.poll() }
        RunLoop.main.add(timer!, forMode: .common)
    }

    // Подхватить уже поднятое соединение (например, после перезапуска программы).
    func adoptExisting(store: Store) {
        guard let info = SystemVPN.current() else { return }
        attach(info.serviceID)
        guard let c = connection else { return }
        let status = SCNetworkConnectionGetStatus(c)
        if status == .connected || status == .connecting {
            active = store.connections.first { $0.server == info.server } ?? store.selected
            activeUser = active?.user ?? ""
            resetStats()
            connectedAt = status == .connected ? estimateConnectTime(c) : nil
            phase = status == .connected ? .connected : .connecting
            poll()
        }
    }

    private func attach(_ id: String) {
        if serviceID == id, connection != nil { return }
        if let c = connection {
            SCNetworkConnectionUnscheduleFromRunLoop(c, CFRunLoopGetMain(), CFRunLoopMode.commonModes.rawValue)
        }
        serviceID = id
        var ctx = SCNetworkConnectionContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(),
                                             retain: nil, release: nil, copyDescription: nil)
        connection = SCNetworkConnectionCreateWithServiceID(nil, id as CFString, { _, status, info in
            guard let info = info else { return }
            Unmanaged<VPNController>.fromOpaque(info).takeUnretainedValue().statusChanged(status)
        }, &ctx)
        if let c = connection {
            SCNetworkConnectionScheduleWithRunLoop(c, CFRunLoopGetMain(), CFRunLoopMode.commonModes.rawValue)
        }
    }

    func connect(_ c: VPNConnection, user: String, password: String, psk: String) {
        guard phase == .idle else { return }
        let parsed = RouteList.parse(c.routes)
        if let bad = parsed.bad {
            banner = Banner(text: "В настройках подключения неверная сеть «\(bad)».", isError: true)
            return
        }
        active = c
        activeUser = user
        banner = nil
        fallbackAllowed = false
        resetStats()
        phase = .connecting
        stage = "Настройка VPN-подключения macOS…"

        // Даём интерфейсу перерисоваться до возможного запроса пароля администратора.
        DispatchQueue.main.async {
            do {
                if SystemVPN.needsApply(server: c.server, routes: parsed.routes, dns: c.dnsServers, dnsDomain: c.dnsDomainName) {
                    try SystemVPN.apply(server: c.server, routes: parsed.routes, dns: c.dnsServers, dnsDomain: c.dnsDomainName)
                }
            } catch SystemVPN.Failure.cancelled {
                self.phase = .idle
                return
            } catch {
                self.fail("Не удалось создать VPN-подключение в системе: \(error.localizedDescription)")
                return
            }
            guard let info = SystemVPN.current() else {
                self.fail("VPN-подключение не найдено в системных настройках.")
                return
            }
            self.attach(info.serviceID)
            guard let conn = self.connection else {
                self.fail("Не удалось открыть VPN-подключение.")
                return
            }
            let options: [String: Any] = [
                kSCEntNetPPP as String: [
                    kSCPropNetPPPAuthName as String: user,
                    kSCPropNetPPPAuthPassword as String: password,
                ],
                kSCEntNetIPSec as String: [
                    kSCPropNetIPSecSharedSecret as String: psk,
                ],
            ]
            self.userStopped = false
            self.stage = "Соединение с сервером \(c.server)…"
            // linger = true: VPN остаётся поднятым, даже если программу закрыть.
            if !SCNetworkConnectionStart(conn, options as CFDictionary, true) {
                let code = SCError()
                self.fail("Не удалось начать подключение: \(String(cString: SCErrorString(code))) (\(code))")
            }
        }
    }

    func disconnect() {
        guard let c = connection, phase == .connected || phase == .connecting else { return }
        userStopped = true
        phase = .disconnecting
        stage = ""
        SCNetworkConnectionStop(c, true)
    }

    private func fail(_ text: String) {
        phase = .idle
        banner = Banner(text: text, isError: true)
    }

    private func statusChanged(_ status: SCNetworkConnectionStatus) {
        switch status {
        case .connecting:
            if phase == .idle { phase = .connecting }
        case .connected:
            phase = .connected
            connectedAt = Date()
            lastSample = nil
            poll()
        case .disconnecting:
            if phase == .connected { phase = .disconnecting }
        case .disconnected, .invalid:
            let was = phase
            let cause = lastCause()
            fallbackAllowed = !userStopped && (was == .connecting || was == .connected || was == .disconnecting) && !lastCauseIsAccount()
            if userStopped {
                if connectedAt != nil {
                    banner = Banner(text: "Соединение завершено. Длительность сессии \(Fmt.duration(duration)).", isError: false)
                }
            } else if was == .connected || was == .disconnecting {
                banner = Banner(text: "Соединение разорвано. " + (cause ?? "Связь с сервером потеряна."), isError: true)
            } else if was == .connecting {
                banner = Banner(text: cause ?? "Не удалось подключиться к серверу.", isError: true)
            }
            userStopped = false
            connectedAt = nil
            phase = .idle
        @unknown default:
            break
        }
    }

    private func poll() {
        guard let c = connection else { return }
        if phase == .connecting, let ext = SCNetworkConnectionCopyExtendedStatus(c) as? [String: Any],
           let ppp = ext[kSCEntNetPPP as String] as? [String: Any], let st = ppp["Status"] as? Int {
            stage = Errors.stage(st, server: active?.server ?? "")
        }
        guard phase == .connected else { return }

        if let stats = SCNetworkConnectionCopyStatistics(c) as? [String: Any],
           let ppp = stats[kSCEntNetPPP as String] as? [String: Any] {
            let inB = (ppp["BytesIn"] as? NSNumber)?.doubleValue ?? 0
            let outB = (ppp["BytesOut"] as? NSNumber)?.doubleValue ?? 0
            let now = Date()
            if let last = lastSample {
                let dt = now.timeIntervalSince(last.date)
                if dt > 0.2 {
                    speedIn = max(0, inB - last.inB) / dt
                    speedOut = max(0, outB - last.outB) / dt
                    historyIn = Array(historyIn.dropFirst()) + [speedIn]
                    historyOut = Array(historyOut.dropFirst()) + [speedOut]
                }
            }
            lastSample = (now, inB, outB)
            bytesIn = inB
            bytesOut = outB
        }
        if let since = connectedAt { duration = Date().timeIntervalSince(since) }
        if ipAddress == nil, let ext = SCNetworkConnectionCopyExtendedStatus(c) as? [String: Any],
           let ipv4 = ext[kSCEntNetIPv4 as String] as? [String: Any],
           let addrs = ipv4[kSCPropNetIPv4Addresses as String] as? [String] {
            ipAddress = addrs.first
        }
    }

    private func resetStats() {
        bytesIn = 0; bytesOut = 0; speedIn = 0; speedOut = 0; duration = 0
        ipAddress = nil
        lastSample = nil
        historyIn = [Double](repeating: 0, count: 60)
        historyOut = [Double](repeating: 0, count: 60)
    }

    // ConnectTime в расширенном статусе PPP — секунды от загрузки системы.
    private func estimateConnectTime(_ c: SCNetworkConnection) -> Date {
        if let ext = SCNetworkConnectionCopyExtendedStatus(c) as? [String: Any],
           let ppp = ext[kSCEntNetPPP as String] as? [String: Any],
           let t = (ppp["ConnectTime"] as? NSNumber)?.doubleValue {
            let uptime = ProcessInfo.processInfo.systemUptime
            if t > 0 && t <= uptime { return Date().addingTimeInterval(-(uptime - t)) }
        }
        return Date()
    }

#if SNAPSHOT
    func testSet(_ phase: Phase, active: VPNConnection?, user: String, stage: String = "", ip: String? = nil,
                 duration: TimeInterval = 0, bytes: (Double, Double) = (0, 0), speed: (Double, Double) = (0, 0),
                 history: ([Double], [Double])? = nil, fallback: Bool = false) {
        self.phase = phase
        self.fallbackAllowed = fallback
        self.active = active
        self.activeUser = user
        self.stage = stage
        self.ipAddress = ip
        self.duration = duration
        (bytesIn, bytesOut) = bytes
        (speedIn, speedOut) = speed
        if let h = history { historyIn = h.0; historyOut = h.1 }
    }
#endif

    // Ошибки учётной записи на другом канале повторятся: 19 — неверный логин/пароль, 11/22 — отказ сервера.
    private func lastCauseIsAccount() -> Bool {
        guard let c = connection, let ext = SCNetworkConnectionCopyExtendedStatus(c) as? [String: Any],
              let ppp = ext[kSCEntNetPPP as String] as? [String: Any] else { return false }
        return [19, 11, 22].contains((ppp["LastCause"] as? Int) ?? 0)
    }

    private func lastCause() -> String? {
        guard let c = connection, let ext = SCNetworkConnectionCopyExtendedStatus(c) as? [String: Any],
              let ppp = ext[kSCEntNetPPP as String] as? [String: Any] else { return nil }
        let cause = (ppp["LastCause"] as? Int) ?? 0
        let device = (ppp["DeviceLastCause"] as? Int) ?? 0
        return Errors.explain(cause: cause, device: device)
    }
}

enum Errors {
    // Коды из ppp_msg.h (EXIT_*) и L2TP DeviceLastCause.
    static func explain(cause: Int, device: Int) -> String? {
        if cause == 0 && device == 0 { return nil }
        if cause == 5 { return nil } // остановлено пользователем
        var text: String
        switch (cause, device) {
        case (_, 5): text = "Не указан общий ключ (PSK)."
        case (_, 1), (_, 2): text = "Сервер не отвечает по L2TP/IPsec: проверьте адрес сервера, общий ключ (PSK) и фаервол (UDP 500, 4500, 1701)."
        case (19, _): text = "Неверный логин или пароль."
        case (11, _), (22, _): text = "Сервер отклонил подключение для этого пользователя."
        case (10, _): text = "Не удалось согласовать параметры PPP с сервером."
        case (15, _), (16, _): text = "Сервер разорвал соединение."
        case (24, _), (8, _): text = "Не удалось установить соединение с сервером: проверьте адрес и общий ключ (PSK)."
        default: text = "Не удалось подключиться."
        }
        return text + " (код \(cause)" + (device != 0 ? "/\(device)" : "") + ")"
    }

    // Состояния PPP: 1–2 соединение (IPsec, L2TP), 4 установка PPP, 5 авторизация, 7–8 сеть.
    static func stage(_ status: Int, server: String) -> String {
        switch status {
        case 0, 1, 2, 3: return "Соединение с сервером \(server) (IPsec)…"
        case 4: return "Установка PPP-сеанса…"
        case 5, 6: return "Проверка логина и пароля…"
        default: return "Настройка сети…"
        }
    }
}
