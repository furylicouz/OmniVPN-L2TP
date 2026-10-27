import Foundation
import Security
import SwiftUI

enum Reach { case checking, up, down }

struct ProbeResult {
    var state: Reach
    var rttMs = 0
    var nameNotFound = false
    var when = Date()
}

// Проверка доступности L2TP/IPsec-сервера: первый пакет IKEv1 Main Mode на UDP 500 (как в Windows-версии).
// Сервер отвечает своим предложением SA — цепочка до него, включая проброс портов, жива.
enum IkeProbe {
    static func check(_ host: String, completion: @escaping (ProbeResult) -> Void) {
        DispatchQueue.global(qos: .utility).async {
            let result = checkSync(host)
            DispatchQueue.main.async { completion(result) }
        }
    }

    static func checkSync(_ host: String) -> ProbeResult {
        guard let address = resolve(host) else { return ProbeResult(state: .down, nameNotFound: true) }
        for _ in 0..<2 {
            let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
            if fd < 0 { continue }
            defer { close(fd) }
            var tv = timeval(tv_sec: 0, tv_usec: 250_000)
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

            var cookie = [UInt8](repeating: 0, count: 8)
            _ = SecRandomCopyBytes(kSecRandomDefault, cookie.count, &cookie)
            let packet = mainMode(cookie)
            var dst = address
            dst.sin_port = UInt16(500).bigEndian
            let start = Date()
            let sent = packet.withUnsafeBytes { buf in
                withUnsafePointer(to: &dst) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        sendto(fd, buf.baseAddress, buf.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
            }
            if sent < 0 { continue }

            var buf = [UInt8](repeating: 0, count: 2048)
            let capacity = buf.count
            while Date().timeIntervalSince(start) < 2 {
                var from = sockaddr_in()
                var len = socklen_t(MemoryLayout<sockaddr_in>.size)
                let n = withUnsafeMutablePointer(to: &from) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, capacity, 0, $0, &len) }
                }
                if n < 0 {
                    if errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR { continue }
                    break
                }
                if n >= 28 && from.sin_addr.s_addr == address.sin_addr.s_addr && Array(buf[0..<8]) == cookie {
                    return ProbeResult(state: .up, rttMs: Int(Date().timeIntervalSince(start) * 1000))
                }
            }
        }
        return ProbeResult(state: .down)
    }

    private static func resolve(_ host: String) -> sockaddr_in? {
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_DGRAM
        var res: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &res) == 0, let first = res else { return nil }
        defer { freeaddrinfo(res) }
        var p: UnsafeMutablePointer<addrinfo>? = first
        while let ai = p {
            if ai.pointee.ai_family == AF_INET, let sa = ai.pointee.ai_addr {
                return sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
            }
            p = ai.pointee.ai_next
        }
        return nil
    }

    // ISAKMP: заголовок + SA (DOI IPsec) + предложение с наборами, которые принимают MikroTik и Windows:
    // AES-128/256 и 3DES, SHA1, MODP2048/1024, общий ключ.
    private static func mainMode(_ cookie: [UInt8]) -> [UInt8] {
        let specs: [(enc: UInt16, keyLen: UInt16, group: UInt16)] = [(7, 128, 14), (7, 256, 14), (7, 128, 2), (5, 0, 2), (5, 0, 14)]
        var transforms: [UInt8] = []
        for (i, s) in specs.enumerated() {
            var attrs: [UInt8] = []
            func attr(_ type: UInt16, _ value: UInt16) {
                attrs += [UInt8(0x80 | (type >> 8)), UInt8(type & 0xFF), UInt8(value >> 8), UInt8(value & 0xFF)]
            }
            attr(1, s.enc)
            if s.keyLen > 0 { attr(14, s.keyLen) }
            attr(2, 2)       // SHA1
            attr(3, 1)       // общий ключ
            attr(4, s.group) // группа DH
            attr(11, 1)      // время жизни в секундах
            attr(12, 28800)
            let len = 8 + attrs.count
            transforms += [i == specs.count - 1 ? 0 : 3, 0, UInt8(len >> 8), UInt8(len & 0xFF), UInt8(i + 1), 1, 0, 0] + attrs
        }
        let propLen = 8 + transforms.count
        let proposal: [UInt8] = [0, 0, UInt8(propLen >> 8), UInt8(propLen & 0xFF), 1, 1, 0, UInt8(specs.count)] + transforms
        let saLen = 12 + proposal.count
        let sa: [UInt8] = [0, 0, UInt8(saLen >> 8), UInt8(saLen & 0xFF), 0, 0, 0, 1, 0, 0, 0, 1] + proposal
        let total = 28 + sa.count
        return cookie + [UInt8](repeating: 0, count: 8) + [1, 0x10, 2, 0, 0, 0, 0, 0,
            UInt8(total >> 24), UInt8((total >> 16) & 0xFF), UInt8((total >> 8) & 0xFF), UInt8(total & 0xFF)] + sa
    }
}

// Доступность каналов и группы «основной → резерв → аварийный» (порядок в списке = приоритет).
final class Channels: ObservableObject {
    @Published private(set) var reach: [String: ProbeResult] = [:]
    // При запуске: если выбран резерв, а основной доступен — выбрать основной (пока пользователь сам не выбрал).
    var autoPrimaryPending = true

    private let store: Store
    private let vpn: VPNController
    private var probing = Set<String>()
    private var timer: Timer?

    init(store: Store, vpn: VPNController) {
        self.store = store
        self.vpn = vpn
        let t = Timer(timeInterval: 45, repeats: true) { [weak self] _ in self?.probeAll(force: false) }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func key(_ server: String) -> String {
        server.trimmingCharacters(in: .whitespaces).lowercased()
    }

    func result(_ c: VPNConnection) -> ProbeResult? {
        reach[key(c.server)]
    }

    // Статус для списка: «доступен, 24 мс» зелёным, «недоступен» красным, «проверка…» серым.
    func status(_ c: VPNConnection) -> (String, Color)? {
        guard let r = result(c) else { return nil }
        switch r.state {
        case .checking: return ("проверка…", Theme.muted)
        case .up: return ("доступен, \(r.rttMs) мс", Theme.ok)
        case .down: return (r.nameNotFound ? "имя не найдено" : "недоступен", Theme.err)
        }
    }

    func group(of c: VPNConnection) -> [VPNConnection] {
        let g = c.groupName
        if g.isEmpty { return [c] }
        return store.connections.filter { $0.groupName.lowercased() == g.lowercased() }
    }

    // Каналы одной группы — одна учётка: если у канала нет своих данных, берём их у другого канала группы.
    func credentialSource(_ c: VPNConnection) -> VPNConnection {
        if c.savePassword && !c.user.isEmpty { return c }
        var withUser: VPNConnection?
        for m in group(of: c) where m.id != c.id && !m.user.isEmpty {
            if !c.user.isEmpty && m.user.lowercased() != c.user.lowercased() { continue }
            if m.savePassword { return m }
            if withUser == nil { withUser = m }
        }
        return withUser ?? c
    }

    // Записать логин и пароль всем каналам группы, кроме тех, у кого явно указан другой логин.
    func shareCredentials(from c: VPNConnection, password: String) {
        for var m in group(of: c) where m.id != c.id {
            if !m.user.isEmpty && m.user.lowercased() != c.user.lowercased() { continue }
            m.user = c.user
            m.savePassword = c.savePassword
            store.upsert(m)
            store.setPassword(c.savePassword ? password : "", m.id)
        }
    }

    // Группа с режимом «меньше задержка»: подключаемся к самому быстрому доступному каналу.
    func isLatencyGroup(_ c: VPNConnection) -> Bool {
        let g = group(of: c)
        return g.count > 1 && g.contains { $0.isLatency }
    }

    // Самый быстрый доступный канал по свежей проверке (кроме except).
    func fastestUp(_ members: [VPNConnection], except: UUID? = nil) -> VPNConnection? {
        members.filter { m in
            guard m.id != except, let r = result(m) else { return false }
            return r.state == .up && Date().timeIntervalSince(r.when) < 180
        }.min { (result($0)?.rttMs ?? .max) < (result($1)?.rttMs ?? .max) }
    }

    // Перемерить каналы без свежего результата (младше 30 с), затем completion на главном потоке.
    func refresh(_ members: [VPNConnection], completion: @escaping () -> Void) {
        let group = DispatchGroup()
        for m in members {
            let host = key(m.server)
            if host.isEmpty { continue }
            if let r = reach[host], r.state != .checking, Date().timeIntervalSince(r.when) < 30 { continue }
            group.enter()
            IkeProbe.check(host) { [weak self] r in
                self?.reach[host] = r
                group.leave()
            }
        }
        group.notify(queue: .main, execute: completion)
    }

    // Более приоритетный канал той же группы, доступный прямо сейчас.
    func better(than c: VPNConnection) -> VPNConnection? {
        if isLatencyGroup(c) { return nil } // в этом режиме канал выбирается сам, не переспрашиваем
        let g = group(of: c)
        guard let idx = g.firstIndex(where: { $0.id == c.id }) else { return nil }
        return g[..<idx].first { x in
            guard let r = result(x) else { return false }
            return r.state == .up && Date().timeIntervalSince(r.when) < 180
        }
    }

    // Следующий канал после неудачного: доступный, иначе не помеченный недоступным, иначе просто следующий.
    func next(after c: VPNConnection) -> VPNConnection? {
        let g = group(of: c)
        if isLatencyGroup(c) {
            // Следующий по скорости из доступных, иначе любой не помеченный недоступным.
            if let f = fastestUp(g, except: c.id) { return f }
            return g.first { $0.id != c.id && result($0)?.state != .down } ?? g.first { $0.id != c.id }
        }
        guard let idx = g.firstIndex(where: { $0.id == c.id }), idx + 1 < g.count else { return nil }
        let rest = g[(idx + 1)...]
        if let up = rest.first(where: { result($0)?.state == .up }) { return up }
        if let unknown = rest.first(where: { result($0)?.state != .down }) { return unknown }
        return rest.first
    }

    func probeAll(force: Bool) {
        if !force && vpn.phase != .idle { return }
        for c in store.connections {
            let host = key(c.server)
            if host.isEmpty { continue }
            if !force, let r = reach[host], r.state != .checking, Date().timeIntervalSince(r.when) < 20 { continue }
            probe(host)
        }
    }

    private func probe(_ host: String) {
        guard !probing.contains(host) else { return }
        probing.insert(host)
        if reach[host] == nil { reach[host] = ProbeResult(state: .checking) }
        IkeProbe.check(host) { [weak self] r in
            guard let self = self else { return }
            self.probing.remove(host)
            self.reach[host] = r
            self.maybeSelectPrimary()
        }
    }

    private func maybeSelectPrimary() {
        guard autoPrimaryPending, vpn.phase == .idle, let c = store.selected else { return }
        let g = group(of: c)
        if isLatencyGroup(c) {
            // Ждём результатов по всем каналам, затем выбираем самый быстрый.
            if g.contains(where: { result($0) == nil || result($0)?.state == .checking }) { return }
            autoPrimaryPending = false
            guard let f = fastestUp(g), f.id != c.id else { return }
            store.selectedID = f.id
            vpn.banner = Banner(text: "Выбран канал с наименьшей задержкой: «\(f.name)» (\(result(f)?.rttMs ?? 0) мс).", isError: false)
            return
        }
        guard g.count > 1, g[0].id != c.id else { autoPrimaryPending = false; return }
        guard let r = result(g[0]), r.state != .checking else { return }
        autoPrimaryPending = false
        guard r.state == .up else { return }
        store.selectedID = g[0].id
        vpn.banner = Banner(text: "Основной канал «\(g[0].name)» доступен — выбран он.", isError: false)
    }

#if SNAPSHOT
    func testSet(_ host: String, _ r: ProbeResult) { reach[key(host)] = r }
#endif
}
