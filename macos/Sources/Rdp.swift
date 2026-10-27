import AppKit
import Combine
import Network
import SwiftUI

// Компьютер для подключения к удалённому рабочему столу.
struct RdpHost: Codable, Identifiable, Equatable {
    var id = UUID()
    var name = ""
    var host = ""       // имя или IP, можно с портом: 192.168.20.25:3390
    var user = ""       // необязательно, например CORP\user
    var vpn = ""        // группа или название VPN, без которого компьютер недоступен
    var fullScreen = true
    var multiMon = false
    // Optional — чтобы читались списки, сохранённые версией 1.5 без этих полей.
    var collection: String?   // alias коллекции RDS (тогда host — брокер подключений)
    var gateway: String?      // шлюз RD Gateway, например gw.example.ru (можно с портом)
    var domain: String?       // домен для логина (CORP); пусто — берётся домен VPN

    var collectionName: String { (collection ?? "").trimmingCharacters(in: .whitespaces) }
    var gatewayName: String { (gateway ?? "").trimmingCharacters(in: .whitespaces) }
    var domainName: String { (domain ?? "").trimmingCharacters(in: .whitespaces) }

    // Что проверять на доступность: шлюз (HTTPS, 443), если он задан, иначе сам компьютер или брокер.
    var probeKey: String { gatewayName.isEmpty ? host.lowercased() : "gw:" + gatewayName.lowercased() }
    var probeHost: String { gatewayName.isEmpty ? hostName : RdpHost.split(gatewayName).0 }
    var probePort: Int { gatewayName.isEmpty ? port : RdpHost.split(gatewayName).1 ?? 443 }

    private static func split(_ s: String) -> (String, Int?) {
        if let i = s.lastIndex(of: ":"), i != s.startIndex { return (String(s[..<i]), Int(s[s.index(after: i)...])) }
        return (s, nil)
    }

    // nil — имя коллекции в порядке, иначе текст ошибки. Alias коллекции — без пробелов.
    static func checkCollection(_ alias: String) -> String? {
        if alias.isEmpty { return nil }
        if alias.rangeOfCharacter(from: CharacterSet(charactersIn: " \t'\"`$;|&<>\r\n:/\\")) != nil {
            return "имя коллекции — это её alias без пробелов (см. Get-RDSessionCollection)."
        }
        return nil
    }

    var hostName: String {
        if let i = host.lastIndex(of: ":"), i != host.startIndex { return String(host[..<i]) }
        return host
    }

    var port: Int {
        if let i = host.lastIndex(of: ":"), let p = Int(host[host.index(after: i)...]) { return p }
        return 3389
    }

    // nil — адрес в порядке, иначе текст ошибки.
    static func checkAddress(_ host: String) -> String? {
        if host.trimmingCharacters(in: .whitespaces).isEmpty { return "не указан адрес компьютера." }
        if host.rangeOfCharacter(from: CharacterSet(charactersIn: " \t'\"`$;|&<>\r\n")) != nil {
            return "недопустимые символы в адресе."
        }
        if let i = host.lastIndex(of: ":") {
            guard i != host.startIndex, let p = Int(host[host.index(after: i)...]), (1...65535).contains(p) else {
                return "неверный порт в адресе (пример: 192.168.20.25:3390)."
            }
        }
        return nil
    }
}

enum RdpLauncher {
    // Приложение Microsoft «Windows App» (бывший Microsoft Remote Desktop) в App Store.
    static let appStoreURL = URL(string: "macappstore://apps.apple.com/app/id1295203466")!

    // Пишет .rdp в кэш и открывает его приложением для RDP. false — такого приложения нет.
    // Пароль не передаём: Windows App сам спросит и сохранит его.
    // Логин для .rdp: «user» + домен CORP -> «CORP\user»; «CORP\user» и «user@corp.local» не меняются.
    static func fullUser(_ user: String, _ domain: String) -> String {
        if user.isEmpty || domain.isEmpty || user.contains("\\") || user.contains("@") { return user }
        return domain + "\\" + user
    }

    static func launch(_ h: RdpHost, domain: String) throws -> Bool {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OmniVPNConnect/rdp", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let safe = h.name.components(separatedBy: CharacterSet(charactersIn: "/:\\")).joined(separator: "_")
        let url = dir.appendingPathComponent(safe + ".rdp")
        try fileText(h, domain: domain).write(to: url, atomically: true, encoding: .utf8)
        guard NSWorkspace.shared.urlForApplication(toOpen: url) != nil else { return false }
        NSWorkspace.shared.open(url)
        return true
    }

    // Содержимое .rdp.
    static func fileText(_ h: RdpHost, domain: String) -> String {
        var s = "full address:s:\(h.host)\r\n"
        if !h.collectionName.isEmpty {
            // Подключение к коллекции RDS через брокер.
            s += "loadbalanceinfo:s:tsv://MS Terminal Services Plugin.1.\(h.collectionName)\r\nuse redirection server name:i:1\r\n"
        }
        if !h.gatewayName.isEmpty {
            // Через шлюз RD Gateway; один логин и пароль для шлюза и компьютера.
            s += "gatewayhostname:s:\(h.gatewayName)\r\ngatewayusagemethod:i:1\r\ngatewayprofileusagemethod:i:1\r\n"
            s += "gatewaycredentialssource:i:0\r\npromptcredentialonce:i:1\r\n"
        }
        // Домен только внутри логина: с отдельной строкой domain:s: клиенты RDP склеивают их в «CORP\CORP\user».
        if !h.user.isEmpty {
            s += "username:s:\(fullUser(h.user, domain))\r\n"
        } else if !domain.isEmpty {
            s += "domain:s:\(domain)\r\n"
        }
        s += "screen mode id:i:\(h.fullScreen ? 2 : 1)\r\n"
        s += "use multimon:i:\(h.multiMon ? 1 : 0)\r\n"
        s += "authentication level:i:2\r\nprompt for credentials:i:0\r\nredirectclipboard:i:1\r\nredirectprinters:i:1\r\n"
        return s
    }

    // Доступность: открывается ли TCP-порт RDP (обычно 3389).
    static func check(_ h: RdpHost, completion: @escaping (ProbeResult) -> Void) {
        guard let port = NWEndpoint.Port(rawValue: UInt16(clamping: h.probePort)) else {
            completion(ProbeResult(state: .down))
            return
        }
        let queue = DispatchQueue(label: "omnivpn.rdp.probe")
        let conn = NWConnection(host: NWEndpoint.Host(h.probeHost), port: port, using: .tcp)
        let start = Date()
        var finished = false
        func finish(_ r: ProbeResult) {
            if finished { return }
            finished = true
            conn.cancel()
            DispatchQueue.main.async { completion(r) }
        }
        conn.stateUpdateHandler = { state in
            switch state {
            case .ready:
                finish(ProbeResult(state: .up, rttMs: Int(Date().timeIntervalSince(start) * 1000)))
            case .failed(let error), .waiting(let error):
                var dns = false
                if case .dns = error { dns = true }
                finish(ProbeResult(state: .down, nameNotFound: dns))
            default:
                break
            }
        }
        conn.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 2) { finish(ProbeResult(state: .down)) }
    }
}

// Список компьютеров, их доступность и запуск (в том числе «сначала подключить VPN»).
final class RdpCenter: ObservableObject {
    @Published private(set) var reach: [String: ProbeResult] = [:]
    private var pending: RdpHost?
    private var pendingSince = Date()

    private let store: Store
    private let vpn: VPNController
    private let channels: Channels
    private let nav: Navigation
    private var probing = Set<String>()
    private var cancellables = Set<AnyCancellable>()
    private var timer: Timer?

    init(store: Store, vpn: VPNController, channels: Channels, nav: Navigation) {
        self.store = store
        self.vpn = vpn
        self.channels = channels
        self.nav = nav
        vpn.$phase.removeDuplicates().receive(on: RunLoop.main)
            .sink { [weak self] phase in self?.phaseChanged(phase) }
            .store(in: &cancellables)
        let t = Timer(timeInterval: 45, repeats: true) { [weak self] _ in
            guard let self = self, self.nav.page == 1 else { return }
            self.probeAll()
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    // VPN, который нужен компьютеру, сейчас подключён (по группе или по названию подключения).
    func vpnReady(_ name: String) -> Bool {
        if name.isEmpty { return true }
        guard vpn.phase == .connected, let a = vpn.active else { return false }
        return a.groupName.lowercased() == name.lowercased() || a.name.lowercased() == name.lowercased()
    }

    func status(_ h: RdpHost) -> (String, Color)? {
        if !vpnReady(h.vpn) { return ("нужен VPN", Theme.muted) }
        guard let r = reach[h.probeKey] else { return nil }
        switch r.state {
        case .checking: return ("проверка…", Theme.muted)
        case .up: return ("доступен, \(r.rttMs) мс", Theme.ok)
        case .down: return (r.nameNotFound ? "имя не найдено" : "недоступен", Theme.err)
        }
    }

    func probeAll() {
        for h in store.rdpHosts where vpnReady(h.vpn) {
            let key = h.probeKey
            if probing.contains(key) { continue }
            probing.insert(key)
            if reach[key] == nil { reach[key] = ProbeResult(state: .checking) }
            RdpLauncher.check(h) { [weak self] r in
                self?.probing.remove(key)
                self?.reach[key] = r
            }
        }
    }

    // Домен для логина RDP: у компьютера, иначе у его VPN-подключения (группы).
    func domain(for h: RdpHost) -> String {
        if !h.domainName.isEmpty { return h.domainName }
        if h.vpn.isEmpty { return "" }
        return store.connections.first {
            ($0.groupName.lowercased() == h.vpn.lowercased() || $0.name.lowercased() == h.vpn.lowercased()) && !$0.domainName.isEmpty
        }?.domainName ?? ""
    }

    // Варианты «Нужен VPN»: группы и подключения без группы.
    var vpnChoices: [String] {
        var list: [String] = []
        for c in store.connections {
            let name = c.groupName.isEmpty ? c.name : c.groupName
            if !list.contains(where: { $0.lowercased() == name.lowercased() }) { list.append(name) }
        }
        return list
    }

    func open(_ h: RdpHost) {
        nav.rdpBanner = nil
        if vpnReady(h.vpn) {
            launch(h)
            return
        }
        if vpn.phase == .connected {
            nav.rdpBanner = Banner(text: "Для «\(h.name)» нужен VPN «\(h.vpn)», а сейчас подключён «\(vpn.active?.name ?? "")». Отключитесь и подключитесь к нужному VPN.", isError: true)
            return
        }
        if vpn.phase != .idle {
            nav.rdpBanner = Banner(text: "Идёт подключение VPN. Дождитесь его и откройте компьютер ещё раз.", isError: true)
            return
        }
        let members = store.connections.filter {
            $0.groupName.lowercased() == h.vpn.lowercased() || $0.name.lowercased() == h.vpn.lowercased()
        }
        guard let first = members.first else {
            nav.rdpBanner = Banner(text: "Для «\(h.name)» указан VPN «\(h.vpn)», но такого подключения нет. Исправьте компьютер или добавьте подключение.", isError: true)
            return
        }
        let alert = NSAlert()
        alert.messageText = "Для «\(h.name)» нужен VPN «\(h.vpn)»."
        alert.informativeText = "Подключиться и открыть компьютер?"
        alert.addButton(withTitle: "Подключиться")
        alert.addButton(withTitle: "Отмена")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        // Основной канал группы, если он доступен; иначе первый доступный; иначе первый по порядку.
        let target = (channels.isLatencyGroup(first) ? channels.fastestUp(members) : nil)
            ?? members.first { channels.result($0)?.state == .up } ?? first
        pending = h
        pendingSince = Date()
        channels.autoPrimaryPending = false
        store.selectedID = target.id
        nav.page = 0

        let src = channels.credentialSource(target)
        let user = target.user.isEmpty ? src.user : target.user
        let password = src.savePassword ? store.password(src.id) : ""
        let psk = store.psk(target.id)
        if !user.isEmpty && !password.isEmpty && !psk.isEmpty {
            vpn.connect(target, user: user, password: password, psk: psk)
        } else {
            vpn.banner = Banner(text: "Введите логин и пароль и нажмите «Подключиться» — «\(h.name)» откроется автоматически.", isError: false)
        }
    }

    private func phaseChanged(_ phase: Phase) {
        objectWillChange.send()
        guard phase == .connected else { return }
        probeAll()
        guard let h = pending, Date().timeIntervalSince(pendingSince) < 600, vpnReady(h.vpn) else { return }
        pending = nil
        // Даём macOS пару секунд прописать маршруты VPN.
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.launch(h) }
    }

    func launch(_ h: RdpHost) {
        do {
            if try RdpLauncher.launch(h, domain: domain(for: h)) { return }
            let alert = NSAlert()
            alert.messageText = "Нужно приложение Windows App"
            alert.informativeText = "Компьютеры открываются бесплатным приложением Microsoft «Windows App» (бывший Microsoft Remote Desktop). Установите его из App Store и откройте компьютер ещё раз."
            alert.addButton(withTitle: "Открыть App Store")
            alert.addButton(withTitle: "Отмена")
            if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(RdpLauncher.appStoreURL) }
        } catch {
            nav.rdpBanner = Banner(text: "Не удалось открыть компьютер: \(error.localizedDescription)", isError: true)
            nav.page = 1
        }
    }
}

// Выпадающий список строк в стиле полей ввода (для «Нужен VPN»).
struct ChoiceSelect: View {
    let items: [String]
    @Binding var selection: Int
    var width: CGFloat
    @State private var anchor: NSView?

    var body: some View {
        Button(action: open) {
            HStack(spacing: 4) {
                Text(items.indices.contains(selection) ? items[selection] : "")
                    .font(.system(size: 13)).foregroundColor(Theme.text).lineLimit(1)
                Spacer(minLength: 4)
                Image(systemName: "chevron.down").font(.system(size: 10, weight: .semibold)).foregroundColor(Theme.label)
            }
            .padding(.horizontal, 8)
            .frame(width: width, height: 30)
            .background(Color.white)
            .overlay(Rectangle().stroke(Theme.fieldBorder))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .background(MenuAnchorView(view: $anchor))
    }

    private func open() {
        guard let view = anchor else { return }
        let menu = NSMenu()
        var actions: [MenuAction] = []
        for (i, title) in items.enumerated() {
            let action = MenuAction { selection = i }
            actions.append(action)
            let mi = NSMenuItem(title: title, action: #selector(MenuAction.fire), keyEquivalent: "")
            mi.target = action
            mi.state = i == selection ? .on : .off
            menu.addItem(mi)
        }
        menu.minimumWidth = width
        menu.font = NSFont.systemFont(ofSize: 13)
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: view.bounds.height + 2), in: view)
        withExtendedLifetime(actions) {}
    }
}

private final class FlippedAnchor: NSView {
    override var isFlipped: Bool { true }
}

struct MenuAnchorView: NSViewRepresentable {
    @Binding var view: NSView?
    func makeNSView(context: Context) -> NSView {
        let v = FlippedAnchor()
        DispatchQueue.main.async { view = v }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

// Вкладка «Удалённые ПК».
struct RdpView: View {
    @EnvironmentObject var store: Store
    @EnvironmentObject var nav: Navigation
    @EnvironmentObject var center: RdpCenter
    @EnvironmentObject var vpn: VPNController
    @State private var editing = false
    @State private var editID: UUID?
    @State private var name = ""
    @State private var host = ""
    @State private var user = ""
    @State private var domain = ""
    @State private var collection = ""
    @State private var gateway = ""
    @State private var vpnIndex = 0
    @State private var fullScreen = true
    @State private var multiMon = false
    @State private var editorBanner: Banner?
    @State private var confirmDelete = false
    @State private var hover: UUID?

    private var choices: [String] { ["Не нужен"] + center.vpnChoices }

    var body: some View {
        Group {
            if editing { editor } else { listView }
        }
        .onAppear {
            center.probeAll()
            openRequestedEditor(nav.rdpEditID)
        }
        .onChange(of: nav.rdpEditID) { openRequestedEditor($0) }
    }

    private var listView: some View {
        VStack(spacing: 0) {
            HStack {
                PageTitle(text: "Удалённые рабочие столы")
                if !store.rdpHosts.isEmpty {
                    Button { edit(nil) } label: { Label("Добавить", systemImage: "plus").fixedSize() }
                        .buttonStyle(LinkButtonStyle())
                        .padding(.top, 20)
                        .padding(.trailing, 26)
                }
            }
            if store.rdpHosts.isEmpty {
                Spacer()
                Text("Добавьте компьютеры, к которым сотрудники подключаются по RDP.\nКлик по компьютеру откроет его в Windows App (Microsoft Remote Desktop).")
                    .multilineTextAlignment(.center)
                    .font(.system(size: 13))
                    .foregroundColor(Theme.label)
                Button("Добавить компьютер") { edit(nil) }
                    .buttonStyle(PrimaryButtonStyle(width: 210))
                    .padding(.top, 18)
                Spacer()
                Spacer()
            } else {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(store.rdpHosts) { h in row(h) }
                    }
                }
                .padding(.horizontal, 14)
                .padding(.top, 8)
                if let b = nav.rdpBanner {
                    MessageBar(banner: b).frame(width: 508).padding(.bottom, 12)
                }
            }
        }
    }

    private func row(_ h: RdpHost) -> some View {
        HStack(spacing: 12) {
            ZStack {
                Circle().fill(Theme.artBack).frame(width: 38, height: 38)
                Image(systemName: "desktopcomputer").font(.system(size: 17)).foregroundColor(Theme.blue)
            }
            VStack(alignment: .leading, spacing: 3) {
                Text(h.name).font(.system(size: 14, weight: .semibold)).foregroundColor(Theme.text).lineLimit(1)
                Text(subtitle(h)).font(.system(size: 11)).foregroundColor(Theme.muted).lineLimit(1)
            }
            Spacer(minLength: 8)
            if let st = center.status(h) {
                HStack(spacing: 5) {
                    Circle().fill(st.1).frame(width: 7, height: 7)
                    Text(st.0).font(.system(size: 11)).foregroundColor(st.1)
                }
                .fixedSize()
            }
            Button { edit(h) } label: {
                Image(systemName: "pencil").font(.system(size: 13)).foregroundColor(Theme.muted).frame(width: 30, height: 30)
            }
            .buttonStyle(.plain)
            .help("Изменить")
        }
        .padding(.horizontal, 12)
        .frame(height: 58)
        .background(hover == h.id ? Color(hex: 0xF3F7FB) : Color.white)
        .overlay(Rectangle().fill(Theme.cardBorder).frame(height: 1), alignment: .bottom)
        .contentShape(Rectangle())
        .onHover { hover = $0 ? h.id : (hover == h.id ? nil : hover) }
        .onTapGesture { center.open(h) }
    }

    // Подсказка в поле «Домен»: домен выбранного VPN, если он задан.
    private var vpnDomainHint: String {
        guard vpnIndex > 0, vpnIndex < choices.count else { return "CORP" }
        var probe = RdpHost()
        probe.vpn = choices[vpnIndex]
        let d = center.domain(for: probe)
        return d.isEmpty ? "CORP" : d
    }

    private func subtitle(_ h: RdpHost) -> String {
        var s = h.host
        if !h.collectionName.isEmpty { s += "  ·  коллекция " + h.collectionName }
        if !h.user.isEmpty { s += "  ·  " + RdpLauncher.fullUser(h.user, center.domain(for: h)) }
        if !h.gatewayName.isEmpty { s += "  ·  шлюз " + h.gatewayName }
        if !h.vpn.isEmpty { s += "  ·  через VPN «\(h.vpn)»" }
        return s
    }

    private var editor: some View {
        VStack(spacing: 0) {
            PageTitle(text: editID == nil ? "Новый компьютер" : "Компьютер")
            VStack(spacing: 10) {
                field("Название") { FlatField(placeholder: "например Бухгалтерия", text: $name) }
                field("Адрес") { FlatField(placeholder: "192.168.20.25, pc01.corp.local или брокер RDS", text: $host) }
                field("Логин") {
                    HStack(spacing: 8) {
                        FlatField(placeholder: "необязательно, например user", text: $user)
                        Text("Домен").font(.system(size: 13)).foregroundColor(Theme.label)
                        FlatField(placeholder: vpnDomainHint, text: $domain).frame(width: 72)
                    }
                }
                field("Коллекция RDS") { FlatField(placeholder: "необязательно: alias коллекции", text: $collection) }
                field("Шлюз RD Gateway") { FlatField(placeholder: "необязательно, например gw.example.ru", text: $gateway) }
                field("Нужен VPN") { ChoiceSelect(items: choices, selection: $vpnIndex, width: 318) }
                HStack(spacing: 24) {
                    Spacer().frame(width: 160)
                    Toggle("Во весь экран", isOn: $fullScreen).toggleStyle(.checkbox)
                    Toggle("Все мониторы", isOn: $multiMon).toggleStyle(.checkbox)
                    Spacer()
                }
                .font(.system(size: 13))
                .foregroundColor(Theme.label)
                .padding(.leading, 40)
                HStack {
                    Spacer().frame(width: 170)
                    Text("Пароль программа не хранит — его спросит и запомнит Windows App (Microsoft Remote Desktop).")
                        .font(.system(size: 11)).foregroundColor(Theme.muted)
                        .frame(width: 318, alignment: .leading)
                    Spacer()
                }
                .padding(.leading, 40)
            }
            .padding(.top, 14)
            Group {
                if let b = editorBanner { MessageBar(banner: b).frame(width: 488) } else { Color.clear.frame(height: 44) }
            }
            .padding(.top, 14)
            Spacer()
            HStack(spacing: 14) {
                if editID != nil {
                    Button { confirmDelete = true } label: { Label("Удалить", systemImage: "trash").fixedSize() }
                        .buttonStyle(LinkButtonStyle(color: Theme.err))
                }
                Spacer()
                Button("Отмена") { editing = false }
                    .buttonStyle(PrimaryButtonStyle(secondary: true, width: 130))
                Button("Сохранить", action: save)
                    .buttonStyle(PrimaryButtonStyle(width: 134))
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 40)
            .padding(.bottom, 24)
        }
        .alert("Удалить компьютер «\(name)»?", isPresented: $confirmDelete) {
            Button("Удалить", role: .destructive) {
                if let id = editID { store.removeRdp(id) }
                editing = false
            }
            Button("Отмена", role: .cancel) {}
        }
    }

    private func field<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 10) {
            Text(title).font(.system(size: 13, weight: .semibold)).foregroundColor(Theme.label).frame(width: 160, alignment: .leading)
            content().frame(width: 318)
            Spacer()
        }
        .padding(.leading, 40)
    }

    private func openRequestedEditor(_ id: UUID?) {
        guard let id = id else { return }
        nav.rdpEditID = nil
        edit(store.rdpHosts.first { $0.id == id })
    }

    private func edit(_ h: RdpHost?) {
        let x = h ?? RdpHost()
        editID = h?.id
        name = x.name
        host = x.host
        user = x.user
        domain = x.domainName
        collection = x.collectionName
        gateway = x.gatewayName
        fullScreen = x.fullScreen
        multiMon = x.multiMon
        if x.vpn.isEmpty {
            vpnIndex = h == nil && choices.count > 1 ? 1 : 0 // новый компьютер обычно доступен через VPN офиса
        } else {
            vpnIndex = choices.firstIndex { $0.lowercased() == x.vpn.lowercased() } ?? 0
        }
        editorBanner = nil
        nav.rdpBanner = nil
        editing = true
    }

    private func save() {
        let n = name.trimmingCharacters(in: .whitespaces)
        let a = host.trimmingCharacters(in: .whitespaces)
        if n.isEmpty { editorBanner = Banner(text: "Укажите название компьютера.", isError: true); return }
        if store.rdpHosts.contains(where: { $0.name.lowercased() == n.lowercased() && $0.id != editID }) {
            editorBanner = Banner(text: "Компьютер с таким названием уже есть.", isError: true); return
        }
        if let error = RdpHost.checkAddress(a) { editorBanner = Banner(text: "Адрес: " + error, isError: true); return }
        let coll = collection.trimmingCharacters(in: .whitespaces)
        let gw = gateway.trimmingCharacters(in: .whitespaces)
        if let error = RdpHost.checkCollection(coll) { editorBanner = Banner(text: "Коллекция: " + error, isError: true); return }
        if !gw.isEmpty, let error = RdpHost.checkAddress(gw) { editorBanner = Banner(text: "Шлюз: " + error, isError: true); return }
        var h = store.rdpHosts.first { $0.id == editID } ?? RdpHost()
        h.name = n
        h.host = a
        h.user = user.trimmingCharacters(in: .whitespaces)
        let d = domain.trimmingCharacters(in: .whitespaces)
        if let error = ProfileFile.checkDomain(d) { editorBanner = Banner(text: "Домен: " + error, isError: true); return }
        h.domain = d.isEmpty ? nil : d
        h.collection = coll.isEmpty ? nil : coll
        h.gateway = gw.isEmpty ? nil : gw
        h.vpn = vpnIndex > 0 && vpnIndex < choices.count ? choices[vpnIndex] : ""
        h.fullScreen = fullScreen
        h.multiMon = multiMon
        store.upsertRdp(h)
        nav.rdpBanner = Banner(text: "Компьютер «\(h.name)» сохранён.", isError: false)
        editing = false
        center.probeAll()
    }
}
