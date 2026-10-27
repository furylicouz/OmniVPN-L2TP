import AppKit
import SwiftUI

enum Theme {
    static let blue = Color(hex: 0x0A6EBD)
    static let blueHover = Color(hex: 0x187BC8)
    static let navy = Color(hex: 0x0B3A63)
    static let navyAccent = Color(hex: 0x8CC4F2)
    static let sideLine = Color(hex: 0x2A82CA)
    static let sideMuted = Color(hex: 0xC4DEF5)
    static let back = Color(hex: 0xEEF2F5)
    static let cardBorder = Color(hex: 0xDDE3E9)
    static let title = Color(hex: 0x4A6072)
    static let label = Color(hex: 0x5D7286)
    static let text = Color(hex: 0x1F2933)
    static let muted = Color(hex: 0x8A99A8)
    static let fieldBorder = Color(hex: 0xC5CFD9)
    static let primary = Color(hex: 0x3D8BD8)
    static let primaryDown = Color(hex: 0x246CB3)
    static let ok = Color(hex: 0x3BAA4A)
    static let busy = Color(hex: 0xE08A1E)
    static let err = Color(hex: 0xD0454C)
    static let errBack = Color(hex: 0xFDEEEE)
    static let okBack = Color(hex: 0xECF7EE)
    static let artBack = Color(hex: 0xE8F1FA)
    static let dots = Color(hex: 0xC5CFD9)
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 255) / 255, green: Double((hex >> 8) & 255) / 255, blue: Double(hex & 255) / 255)
    }
}

final class Navigation: ObservableObject {
    @Published var page = 0
    @Published var editID: UUID?
    @Published var editToken = 0
    @Published var rdpBanner: Banner?
    @Published var rdpEditID: UUID?

    // Страницы: 0 — удалённый доступ, 1 — удалённые ПК, 2 — подключения, 3 — о программе.
    func edit(_ id: UUID?) {
        editID = id
        editToken += 1
        page = 2
    }
}

// ---------- Общие элементы ----------

struct PrimaryButtonStyle: ButtonStyle {
    var secondary = false
    var width: CGFloat = 160
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        let fill: Color = secondary
            ? (configuration.isPressed ? Color(hex: 0xE6ECF2) : .white)
            : (!enabled ? Theme.primary.opacity(0.5) : configuration.isPressed ? Theme.primaryDown : Theme.primary)
        return configuration.label
            .font(.system(size: 14, weight: .semibold))
            .foregroundColor(secondary ? Theme.title.opacity(enabled ? 1 : 0.45) : .white)
            .frame(width: width, height: 38)
            .background(RoundedRectangle(cornerRadius: 3).fill(fill))
            .overlay(RoundedRectangle(cornerRadius: 3).stroke(secondary ? Theme.fieldBorder : .clear))
            .contentShape(Rectangle())
    }
}

struct LinkButtonStyle: ButtonStyle {
    var color = Theme.primary
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13))
            .foregroundColor(configuration.isPressed ? Theme.primaryDown : color)
            .contentShape(Rectangle())
    }
}

struct FlatField: View {
    var placeholder = ""
    @Binding var text: String
    var secure = false
    @State private var reveal = false
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 4) {
            Group {
                if secure && !reveal {
                    SecureField(placeholder, text: $text)
                } else {
                    TextField(placeholder, text: $text)
                }
            }
            .textFieldStyle(.plain)
            .font(.system(size: 13))
            .foregroundColor(Theme.text)
            .focused($focused)
            if secure {
                Button { reveal.toggle() } label: {
                    Image(systemName: reveal ? "eye.slash" : "eye").foregroundColor(Theme.muted)
                }
                .buttonStyle(.plain)
                .help("Показать / скрыть")
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 30)
        .background(Color.white)
        .overlay(Rectangle().stroke(focused ? Theme.primary : Theme.fieldBorder, lineWidth: 1))
    }
}

// Выпадающий список в стиле полей ввода: системный Picker выбивается из дизайна.
struct FlatSelect: View {
    let items: [VPNConnection]
    @Binding var selection: UUID?
    var width: CGFloat
    var status: (VPNConnection) -> (String, Color)? = { _ in nil }
    var onOpen: () -> Void = {}
    @State private var anchor: NSView?
    @State private var hover = false

    var body: some View {
        Button(action: open) {
            HStack(spacing: 4) {
                Text(items.first { $0.id == selection }?.name ?? "")
                    .font(.system(size: 13))
                    .foregroundColor(Theme.text)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if let c = items.first(where: { $0.id == selection }), let st = status(c) {
                    HStack(spacing: 4) {
                        Circle().fill(st.1).frame(width: 7, height: 7)
                        Text(st.0.components(separatedBy: ",")[0]).font(.system(size: 11)).foregroundColor(st.1)
                    }
                    .fixedSize()
                }
                Image(systemName: "chevron.down").font(.system(size: 10, weight: .semibold)).foregroundColor(Theme.label)
            }
            .padding(.horizontal, 8)
            .frame(width: width, height: 30)
            .background(hover ? Color(hex: 0xF7F9FB) : Color.white)
            .overlay(Rectangle().stroke(Theme.fieldBorder))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .background(MenuAnchor(view: $anchor))
        .onHover { hover = $0 }
        .disabled(items.isEmpty)
    }

    private func open() {
        guard let view = anchor else { return }
        let menu = NSMenu()
        var actions: [MenuAction] = []
        for item in items {
            let action = MenuAction { selection = item.id }
            actions.append(action)
            let mi = NSMenuItem(title: item.name, action: #selector(MenuAction.fire), keyEquivalent: "")
            mi.target = action
            mi.state = item.id == selection ? .on : .off
            if let st = status(item) {
                // Название, затем статус цветом справа: «●  доступен, 24 мс».
                let title = NSMutableAttributedString(string: item.name + "\t",
                                                      attributes: [.font: NSFont.systemFont(ofSize: 13)])
                title.append(NSAttributedString(string: "●  " + st.0, attributes: [
                    .font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor(st.1)]))
                let para = NSMutableParagraphStyle()
                para.tabStops = [NSTextTab(textAlignment: .right, location: max(width - 30, 260))]
                title.addAttribute(.paragraphStyle, value: para, range: NSRange(location: 0, length: title.length))
                mi.attributedTitle = title
            }
            menu.addItem(mi)
        }
        onOpen()
        menu.minimumWidth = width
        menu.font = NSFont.systemFont(ofSize: 13)
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: view.bounds.height + 2), in: view)
        withExtendedLifetime(actions) {}
    }
}

final class MenuAction: NSObject {
    let handler: () -> Void
    init(_ handler: @escaping () -> Void) { self.handler = handler }
    @objc func fire() { handler() }
}

private final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

// Ссылка-кнопка, открывающая меню из нескольких действий (например, «Экспорт»).
struct PopupMenuButton<Content: View>: View {
    let items: [(String, () -> Void)]
    let content: Content
    @State private var anchor: NSView?

    init(items: [(String, () -> Void)], @ViewBuilder label: () -> Content) {
        self.items = items
        self.content = label()
    }

    var body: some View {
        Button(action: open) { content }
            .buttonStyle(LinkButtonStyle())
            .background(MenuAnchor(view: $anchor))
    }

    private func open() {
        guard let view = anchor else { return }
        let menu = NSMenu()
        var actions: [MenuAction] = []
        for (title, handler) in items {
            let action = MenuAction(handler)
            actions.append(action)
            let mi = NSMenuItem(title: title, action: #selector(MenuAction.fire), keyEquivalent: "")
            mi.target = action
            menu.addItem(mi)
        }
        menu.font = NSFont.systemFont(ofSize: 13)
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: view.bounds.height + 2), in: view)
        withExtendedLifetime(actions) {}
    }
}

private struct MenuAnchor: NSViewRepresentable {
    @Binding var view: NSView?
    func makeNSView(context: Context) -> NSView {
        let v = FlippedView()
        DispatchQueue.main.async { view = v }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

struct MessageBar: View {
    let banner: Banner

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: banner.isError ? "exclamationmark.circle" : "checkmark.circle")
                .font(.system(size: 16))
                .foregroundColor(banner.isError ? Theme.err : Theme.ok)
            Text(banner.text)
                .font(.system(size: 13))
                .foregroundColor(Theme.text)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(minHeight: 44)
        .background(RoundedRectangle(cornerRadius: 3).fill(banner.isError ? Theme.errBack : Theme.okBack))
        .overlay(RoundedRectangle(cornerRadius: 3).stroke((banner.isError ? Theme.err : Theme.ok).opacity(0.4)))
    }
}

struct PageTitle: View {
    let text: String
    var body: some View {
        Text(text)
            .font(.system(size: 25, weight: .light))
            .foregroundColor(Theme.title)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.leading, 30)
            .padding(.top, 20)
    }
}

// ---------- Иллюстрация «ноутбук ● ● ● глобус» ----------

enum ArtMode { case idle, connecting, connected, error }

struct NetArt: View {
    let mode: ArtMode

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: mode != .connecting)) { ctx in
            art(ctx.date.timeIntervalSinceReferenceDate)
        }
        .frame(width: 300, height: 150)
    }

    private func art(_ t: Double) -> some View {
        let iconColor = mode == .error ? Theme.blue.opacity(0.75) : Theme.blue
        return ZStack {
            Circle().fill(Theme.artBack).frame(width: 96, height: 96).position(x: 58, y: 92)
            Image(systemName: "laptopcomputer").font(.system(size: 40)).foregroundColor(iconColor).position(x: 58, y: 94)
            Circle().fill(Theme.artBack).frame(width: 96, height: 96).position(x: 242, y: 92)
            Image(systemName: "globe").font(.system(size: 46, weight: .light)).foregroundColor(iconColor).position(x: 242, y: 92)
            ForEach(0..<9, id: \.self) { i in
                let k = glow(i, t)
                Circle()
                    .fill(dotColor(k))
                    .frame(width: 7 * (1 + 0.35 * k), height: 7 * (1 + 0.35 * k))
                    .position(x: 116 + CGFloat(i) * 8.5, y: 92)
            }
            Circle().fill(Color.white).frame(width: 46, height: 46).position(x: 150, y: 46)
            Circle().fill(badgeColor).frame(width: 38, height: 38).position(x: 150, y: 46)
            Image(systemName: badgeSymbol).font(.system(size: 15, weight: .semibold)).foregroundColor(.white).position(x: 150, y: 46)
        }
    }

    private func glow(_ i: Int, _ t: Double) -> Double {
        guard mode == .connecting else { return 0 }
        var phase = (t * 9 - Double(i)).truncatingRemainder(dividingBy: 12)
        if phase < 0 { phase += 12 }
        return phase < 3 ? 1 - abs(phase - 1.5) / 1.5 : 0
    }

    private func dotColor(_ k: Double) -> Color {
        switch mode {
        case .connected: return Theme.ok
        case .error: return Theme.err.opacity(0.65)
        case .connecting: return k > 0.05 ? Theme.primary.opacity(0.35 + 0.65 * k) : Theme.dots
        case .idle: return Theme.dots
        }
    }

    private var badgeColor: Color {
        switch mode {
        case .connected: return Theme.ok
        case .error: return Theme.err
        case .connecting: return Theme.primary
        case .idle: return Theme.muted
        }
    }

    private var badgeSymbol: String {
        switch mode {
        case .connected, .connecting: return "lock.fill"
        case .error: return "xmark"
        case .idle: return "lock.open.fill"
        }
    }
}

struct ProgressStripe: View {
    var body: some View {
        TimelineView(.animation) { ctx in
            GeometryReader { geo in
                let w = geo.size.width
                let seg = w * 0.3
                let t = ctx.date.timeIntervalSinceReferenceDate * 0.8
                let x = CGFloat(t.truncatingRemainder(dividingBy: 1)) * (w + seg) - seg
                ZStack(alignment: .leading) {
                    Rectangle().fill(Color(hex: 0xE3EAF1))
                    Rectangle().fill(Theme.primary).frame(width: seg).offset(x: x)
                }
                .clipped()
            }
        }
        .frame(width: 200, height: 4)
    }
}

struct SpeedGraph: View {
    let rx: [Double]
    let tx: [Double]

    var body: some View {
        let maxV = max(1024, rx.max() ?? 0, tx.max() ?? 0) * 1.15
        VStack(spacing: 4) {
            HStack(spacing: 6) {
                Text("Скорость за минуту, до \(Fmt.speed(maxV))")
                Spacer()
                legend("приём", Theme.ok)
                legend("передача", Theme.primary)
            }
            .font(.system(size: 11))
            .foregroundColor(Theme.muted)
            Canvas { ctx, size in
                ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(Color(hex: 0xF7F9FB)))
                for (data, color) in [(tx, Theme.primary), (rx, Theme.ok)] {
                    var line = Path()
                    for (i, v) in data.enumerated() {
                        let p = CGPoint(x: size.width * CGFloat(i) / CGFloat(data.count - 1),
                                        y: size.height - CGFloat(v / maxV) * size.height)
                        if i == 0 { line.move(to: p) } else { line.addLine(to: p) }
                    }
                    var area = line
                    area.addLine(to: CGPoint(x: size.width, y: size.height))
                    area.addLine(to: CGPoint(x: 0, y: size.height))
                    area.closeSubpath()
                    ctx.fill(area, with: .color(color.opacity(0.18)))
                    ctx.stroke(line, with: .color(color), lineWidth: 1.5)
                }
                ctx.stroke(Path { $0.move(to: CGPoint(x: 0, y: size.height - 0.5)); $0.addLine(to: CGPoint(x: size.width, y: size.height - 0.5)) },
                           with: .color(Theme.cardBorder))
            }
        }
        .frame(width: 400, height: 58)
    }

    private func legend(_ text: String, _ color: Color) -> some View {
        HStack(spacing: 4) {
            Rectangle().fill(color).frame(width: 8, height: 8)
            Text(text)
        }
    }
}

// ---------- Окно ----------

struct MainView: View {
    @EnvironmentObject var store: Store
    @EnvironmentObject var vpn: VPNController
    @EnvironmentObject var nav: Navigation

    var body: some View {
        HStack(spacing: 0) {
            SidebarView().frame(width: 230)
            ZStack {
                Theme.back
                Group {
                    switch nav.page {
                    case 1: RdpView()
                    case 2: ConnectionsView()
                    case 3: AboutView()
                    default: RemoteView()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color.white)
                .overlay(Rectangle().stroke(Theme.cardBorder))
                .padding(10)
            }
        }
        .frame(width: 820, height: 540)
    }
}

struct SidebarView: View {
    @EnvironmentObject var store: Store
    @EnvironmentObject var vpn: VPNController
    @EnvironmentObject var nav: Navigation
    @EnvironmentObject var net: NetMonitor
    @EnvironmentObject var updater: Updater
    @State private var hover: Int?

    private let items: [(String, String)] = [("УДАЛЁННЫЙ ДОСТУП", "laptopcomputer"), ("Удалённые ПК", "desktopcomputer"),
                                             ("Подключения", "gearshape"), ("О программе", "info.circle")]

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                Circle().fill(Color.white.opacity(0.27)).frame(width: 100, height: 100)
                Circle().fill(Theme.artBack).frame(width: 92, height: 92)
                Image(systemName: "person").font(.system(size: 42, weight: .light)).foregroundColor(Theme.blue)
            }
            .padding(.top, 30)
            Text(userName)
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(.white)
                .lineLimit(1)
                .padding(.top, 10)
                .padding(.horizontal, 8)
            HStack(spacing: 6) {
                Circle().fill(statusColor).frame(width: 8, height: 8)
                Text(statusText).font(.system(size: 11)).foregroundColor(Theme.sideMuted)
            }
            .padding(.top, 6)
            .padding(.bottom, 18)
            Rectangle().fill(Theme.sideLine).frame(height: 1)
            ForEach(items.indices, id: \.self) { i in
                navItem(i)
                Rectangle().fill(Theme.sideLine).frame(height: 1)
            }
            Spacer()
            Rectangle().fill(Theme.sideLine).frame(height: 1)
            VStack(spacing: 7) {
                netRow("Интернет", net.internetText, net.internetColor)
                netRow("VPN-сервер", net.serverText, net.serverColor)
                netRow("Версия", appVersion, updater.available != nil ? Theme.navyAccent : Theme.ok)
            }
            .padding(.horizontal, 18)
            .padding(.top, 12)
            .padding(.bottom, 10)
            if let u = updater.available {
                // Кнопка-ссылка на «О программе», где идёт обновление.
                Button { nav.page = 3 } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "arrow.down.circle")
                        Text(updateText(u)).lineLimit(1)
                    }
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(.white)
                    .frame(maxWidth: .infinity)
                    .frame(height: 26)
                    .background(RoundedRectangle(cornerRadius: 4).fill(Theme.navy))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .padding(.horizontal, 14)
                .padding(.bottom, 12)
            } else {
                Text("L2TP/IPsec  ·  OmniVPN Connect")
                    .font(.system(size: 11))
                    .foregroundColor(Theme.sideMuted)
                    .padding(.bottom, 14)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.blue)
    }

    private func updateText(_ u: UpdateInfo) -> String {
        switch updater.phase {
        case .downloading: return "Обновление: \(Int(updater.progress * 100))%"
        case .installing: return "Установка \(u.version)…"
        default: return "Доступна версия \(u.version)"
        }
    }

    // «● Интернет — работает, 12 мс» и «● VPN-сервер — 24 мс».
    private func netRow(_ label: String, _ value: String, _ color: Color) -> some View {
        HStack(spacing: 8) {
            Circle().fill(color).frame(width: 8, height: 8)
            Text(label).foregroundColor(Theme.sideMuted)
            Spacer(minLength: 4)
            Text(value).foregroundColor(.white).lineLimit(1)
        }
        .font(.system(size: 11))
    }

    private func navItem(_ i: Int) -> some View {
        let selected = nav.page == i
        let fore = selected ? Color.white : Theme.sideMuted
        return Button { nav.page = i } label: {
            HStack(spacing: 12) {
                ZStack(alignment: .bottomTrailing) {
                    Image(systemName: items[i].1).font(.system(size: 17))
                    if i == 0 {
                        Image(systemName: "lock.fill").font(.system(size: 8, weight: .bold))
                            .padding(2)
                            .background(Circle().fill(selected ? Theme.navy : hover == i ? Theme.blueHover : Theme.blue))
                            .offset(x: 6, y: 4)
                    }
                }
                .frame(width: 28)
                Text(items[i].0).font(.system(size: 13.5, weight: selected ? .semibold : .regular))
                Spacer()
            }
            .foregroundColor(fore)
            .padding(.leading, 18)
            .frame(height: 51)
            .background(selected ? Theme.navy : hover == i ? Theme.blueHover : Color.clear)
            .overlay(Rectangle().fill(selected ? Theme.navyAccent : .clear).frame(width: 4), alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 ? i : (hover == i ? nil : hover) }
    }

    private var userName: String {
        if vpn.phase != .idle, !vpn.activeUser.isEmpty { return vpn.activeUser }
        if let u = store.selected?.user, !u.isEmpty { return u }
        return NSFullUserName().isEmpty ? NSUserName() : NSFullUserName()
    }

    private var statusText: String {
        switch vpn.phase {
        case .idle: return "Не подключено"
        case .connecting: return "Подключение…"
        case .connected: return "Подключено"
        case .disconnecting: return "Отключение…"
        }
    }

    private var statusColor: Color {
        switch vpn.phase {
        case .idle: return Theme.dots
        case .connected: return Theme.ok
        default: return Theme.busy
        }
    }
}

var appVersion: String {
    Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
}

// ---------- Удалённый доступ ----------

struct RemoteView: View {
    @EnvironmentObject var store: Store
    @EnvironmentObject var vpn: VPNController
    @EnvironmentObject var nav: Navigation
    @EnvironmentObject var profiles: ProfileIO
    @EnvironmentObject var channels: Channels
    @State private var user = ""
    @State private var password = ""
    @State private var savePassword = false
    @State private var localBanner: Banner?
    @State private var keepCredentials = false

    var body: some View {
        Group {
            switch vpn.phase {
            case .connected: connectedView
            case .connecting, .disconnecting: connectingView
            case .idle: store.connections.isEmpty ? AnyView(emptyView) : AnyView(loginView)
            }
        }
        .onAppear(perform: loadCredentials)
        .onChange(of: store.selectedID) { _ in loadCredentials() }
    }

    private var banner: Banner? { localBanner ?? vpn.banner }

    private var loginView: some View {
        VStack(spacing: 0) {
            NetArt(mode: vpn.banner?.isError == true ? .error : .idle).padding(.top, 22)
            VStack(spacing: 10) {
                row("Подключение") {
                    HStack(spacing: 8) {
                        FlatSelect(items: store.connections,
                                   selection: Binding(get: { store.selected?.id }, set: {
                                       channels.autoPrimaryPending = false
                                       store.selectedID = $0
                                   }),
                                   width: 240,
                                   status: { channels.status($0) },
                                   onOpen: { channels.probeAll(force: true) })
                        Button { nav.edit(store.selected?.id) } label: { Label("Настроить", systemImage: "gearshape") }
                            .buttonStyle(LinkButtonStyle())
                    }
                }
                row("Логин") { FlatField(text: $user).frame(width: 240) }
                row("Пароль") { FlatField(text: $password, secure: true).frame(width: 240) }
                row("") {
                    Toggle("Сохранить пароль", isOn: $savePassword)
                        .toggleStyle(.checkbox)
                        .font(.system(size: 13))
                        .foregroundColor(Theme.label)
                }
            }
            .padding(.top, 18)
            Group {
                if let b = banner { MessageBar(banner: b).frame(width: 428) } else { Color.clear.frame(height: 44) }
            }
            .padding(.top, 10)
            Button("Подключиться") { connect(skipGuard: false) }
                .buttonStyle(PrimaryButtonStyle())
                .keyboardShortcut(.defaultAction)
                .padding(.top, 14)
            if let next = fallbackTarget {
                Button("Подключиться через «\(next.name)»") { connectFallback(next) }
                    .buttonStyle(PrimaryButtonStyle(secondary: true, width: 320))
                    .padding(.top, 10)
            }
            Spacer()
        }
    }

    private var fallbackTarget: VPNConnection? {
        guard vpn.phase == .idle, vpn.fallbackAllowed, localBanner == nil, let a = vpn.active else { return nil }
        return channels.next(after: a)
    }

    private func connectFallback(_ next: VPNConnection) {
        channels.autoPrimaryPending = false
        keepCredentials = true
        store.selectedID = next.id
        connect(skipGuard: true)
    }

    private func row<Content: View>(_ label: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 10) {
            Text(label)
                .font(.system(size: 13))
                .foregroundColor(Theme.label)
                .frame(width: 180, alignment: .trailing)
            content()
            Spacer(minLength: 0)
        }
        .padding(.leading, 30)
    }

    private var connectingView: some View {
        VStack(spacing: 0) {
            NetArt(mode: .connecting).padding(.top, 22)
            Text(vpn.phase == .disconnecting ? "Отключение…" : "Подключение к «\(vpn.active?.name ?? "")»")
                .font(.system(size: 21, weight: .light))
                .foregroundColor(Theme.title)
                .padding(.top, 26)
            Text(vpn.stage)
                .font(.system(size: 13))
                .foregroundColor(Theme.label)
                .padding(.top, 6)
            ProgressStripe().padding(.top, 20)
            Button("Отмена") { vpn.disconnect() }
                .buttonStyle(PrimaryButtonStyle(secondary: true))
                .disabled(vpn.phase == .disconnecting)
                .padding(.top, 22)
            Spacer()
        }
    }

    private var connectedView: some View {
        VStack(spacing: 0) {
            PageTitle(text: "VPN подключён")
            NetArt(mode: .connected).frame(height: 134).padding(.top, 2)
            VStack(spacing: 6) {
                info("Подключение", vpn.active?.name ?? "—")
                info("VPN-сервер", vpn.active?.server ?? "—")
                info("IP-адрес", vpn.ipAddress ?? "получение…")
                info("Логин", vpn.activeUser.isEmpty ? "—" : vpn.activeUser)
                info("Длительность", Fmt.duration(vpn.duration))
                info("Получено", Fmt.bytes(vpn.bytesIn), "↓ " + Fmt.speed(vpn.speedIn))
                info("Отправлено", Fmt.bytes(vpn.bytesOut), "↑ " + Fmt.speed(vpn.speedOut))
            }
            .padding(.top, 10)
            SpeedGraph(rx: vpn.historyIn, tx: vpn.historyOut).padding(.top, 12)
            Button("Отключиться") { vpn.disconnect() }
                .buttonStyle(PrimaryButtonStyle())
                .padding(.top, 12)
            Spacer()
        }
    }

    private func info(_ label: String, _ value: String, _ extra: String? = nil) -> some View {
        HStack(spacing: 14) {
            Text(label).foregroundColor(Theme.label).frame(width: 180, alignment: .trailing)
            Text(value).foregroundColor(Theme.text)
            if let extra = extra { Text(extra).foregroundColor(Theme.muted).padding(.leading, 4) }
            Spacer()
        }
        .font(.system(size: 13.5))
        .padding(.leading, 24)
    }

    private var emptyView: some View {
        VStack(spacing: 0) {
            NetArt(mode: .idle).padding(.top, 22)
            Text("Ещё нет ни одного VPN-подключения.\nДобавьте его вручную или откройте файл профиля от администратора.")
                .multilineTextAlignment(.center)
                .font(.system(size: 13))
                .foregroundColor(Theme.label)
                .padding(.top, 28)
            Button("Добавить подключение") { nav.edit(nil) }
                .buttonStyle(PrimaryButtonStyle(width: 210))
                .padding(.top, 18)
            Button { profiles.importWithPanel() } label: {
                Label("Открыть файл профиля (.omnivpn)", systemImage: "square.and.arrow.down")
            }
            .buttonStyle(LinkButtonStyle())
            .padding(.top, 14)
            Spacer()
        }
    }

    private func loadCredentials() {
        // Переход на резерв оставляет введённые логин и пароль (учётка у каналов офиса общая).
        if keepCredentials {
            keepCredentials = false
            return
        }
        guard let c = store.selected else { return }
        let src = channels.credentialSource(c)
        user = c.user.isEmpty ? src.user : c.user
        savePassword = src.savePassword
        password = src.savePassword ? store.password(src.id) : ""
        localBanner = nil
    }

    private func connect(skipGuard: Bool) {
        guard var c = store.selected else { return }
        localBanner = nil
        let u = user.trimmingCharacters(in: .whitespaces)
        if u.isEmpty { localBanner = Banner(text: "Укажите логин.", isError: true); return }
        if password.isEmpty { localBanner = Banner(text: "Укажите пароль.", isError: true); return }

        // Режим «меньше задержка»: перемерить каналы группы и подключиться к самому быстрому.
        if !skipGuard && channels.isLatencyGroup(c) {
            let members = channels.group(of: c)
            localBanner = Banner(text: "Выбираю канал с наименьшей задержкой…", isError: false)
            channels.refresh(members) {
                localBanner = nil
                if let f = channels.fastestUp(members), f.id != c.id {
                    keepCredentials = true
                    store.selectedID = f.id
                }
                connect(skipGuard: true)
            }
            return
        }

        if !skipGuard, let better = channels.better(than: c) {
            let alert = NSAlert()
            alert.messageText = "Основной канал «\(better.name)» сейчас доступен."
            alert.informativeText = "Подключиться через основной канал вместо «\(c.name)»?"
            alert.addButton(withTitle: "Через «\(better.name)»")
            alert.addButton(withTitle: "Всё равно через «\(c.name)»")
            alert.addButton(withTitle: "Отмена")
            switch alert.runModal() {
            case .alertFirstButtonReturn:
                keepCredentials = true
                store.selectedID = better.id
                c = better
            case .alertSecondButtonReturn:
                break
            default:
                return
            }
        }

        let psk = store.psk(c.id)
        if c.server.isEmpty || psk.isEmpty {
            localBanner = Banner(text: "В настройках подключения не указан сервер или общий ключ.", isError: true)
            return
        }
        c.user = u
        c.savePassword = savePassword
        store.upsert(c)
        store.setPassword(savePassword ? password : "", c.id)
        channels.shareCredentials(from: c, password: password)
        vpn.connect(c, user: u, password: password, psk: psk)
    }
}

// ---------- Подключения ----------

struct ConnectionsView: View {
    @EnvironmentObject var store: Store
    @EnvironmentObject var vpn: VPNController
    @EnvironmentObject var nav: Navigation
    @EnvironmentObject var profiles: ProfileIO
    @State private var editID: UUID?
    @State private var name = ""
    @State private var server = ""
    @State private var psk = ""
    @State private var routes = ""
    @State private var user = ""
    @State private var group = ""
    @State private var domain = ""
    @State private var balanceIndex = 0
    @State private var dns = ""
    @State private var dnsDomain = ""
    @State private var banner: Banner?
    @State private var confirmDelete = false

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 18) {
                PageTitle(text: editID == nil ? "Новое подключение" : "Настройка подключения")
                Button { profiles.importWithPanel() } label: {
                    Label("Импорт", systemImage: "square.and.arrow.down").fixedSize()
                }
                .buttonStyle(LinkButtonStyle())
                .padding(.top, 20)
                PopupMenuButton(items: exportItems) {
                    Label("Экспорт", systemImage: "square.and.arrow.up").fixedSize()
                }
                .padding(.top, 20)
                .padding(.trailing, 24)
                .disabled(store.connections.isEmpty)
            }
            HStack(spacing: 10) {
                label("Подключение")
                FlatSelect(items: store.connections, selection: Binding(get: { editID }, set: { load($0) }), width: 196)
                Button { load(nil) } label: { Label("Новое", systemImage: "plus").fixedSize() }.buttonStyle(LinkButtonStyle())
                Button { confirmDelete = true } label: { Label("Удалить", systemImage: "trash").fixedSize() }
                    .buttonStyle(LinkButtonStyle(color: Theme.err))
                    .disabled(editID == nil)
                Spacer()
            }
            .padding(.leading, 40)
            .padding(.top, 16)
            Rectangle().fill(Theme.cardBorder).frame(height: 1).padding(.horizontal, 40).padding(.top, 14)
            VStack(spacing: 10) {
                field("Название") { FlatField(placeholder: "например Офис", text: $name) }
                field("VPN-сервер") { FlatField(placeholder: "vpn.example.ru или 203.0.113.10", text: $server) }
                field("Общий ключ (PSK)") { FlatField(text: $psk, secure: true) }
                field("Сети за VPN") { FlatField(placeholder: "например 192.168.88.0/24, 10.10.0.0/16", text: $routes) }
                HStack {
                    Spacer().frame(width: 210)
                    Text("Необязательно. Интернет идёт напрямую, не через VPN.")
                        .font(.system(size: 11))
                        .foregroundColor(Theme.muted)
                        .frame(width: 318, alignment: .leading)
                    Spacer()
                }
                .padding(.leading, 40)
                .padding(.top, -4)
                field("Логин") {
                    HStack(spacing: 8) {
                        FlatField(placeholder: "можно указать при подключении", text: $user)
                        Text("Домен").font(.system(size: 13)).foregroundColor(Theme.label)
                        FlatField(placeholder: "CORP", text: $domain).frame(width: 72)
                    }
                }
                field("Группа") {
                    HStack(spacing: 8) {
                        FlatField(placeholder: "напр. Офис", text: $group)
                        ChoiceSelect(items: ["По порядку", "Меньше задержка"], selection: $balanceIndex, width: 162)
                    }
                }
                field("DNS") {
                    HStack(spacing: 8) {
                        FlatField(placeholder: "192.168.10.11, 192.168.10.12", text: $dns)
                        FlatField(placeholder: "домен: corp.local", text: $dnsDomain).frame(width: 132)
                    }
                }
            }
            .padding(.top, 14)
            Group {
                if let b = banner { MessageBar(banner: b).frame(width: 488) } else { Color.clear.frame(height: 44) }
            }
            .padding(.top, 12)
            Spacer()
            HStack(spacing: 20) {
                Button("Отмена") { load(store.selected?.id); nav.page = 0 }
                    .buttonStyle(PrimaryButtonStyle(secondary: true))
                Button("Сохранить", action: save)
                    .buttonStyle(PrimaryButtonStyle())
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.bottom, 26)
        }
        .onAppear { load(nav.editID ?? store.selected?.id) }
        .onChange(of: nav.editToken) { _ in load(nav.editID) }
        .alert("Удалить подключение «\(name)»?", isPresented: $confirmDelete) {
            Button("Удалить", role: .destructive, action: delete)
            Button("Отмена", role: .cancel) {}
        }
    }

    private var exportItems: [(String, () -> Void)] {
        var items: [(String, () -> Void)] = []
        if let c = store.connections.first(where: { $0.id == editID }) ?? store.selected {
            items.append(("Подключение «\(c.name)»", { banner = profiles.export([c], baseName: c.name) }))
        }
        let pcs = store.rdpHosts.isEmpty ? "" : " и компьютеры (\(store.rdpHosts.count))"
        items.append(("Все подключения (\(store.connections.count))" + pcs, {
            banner = profiles.export(store.connections, rdp: store.rdpHosts, baseName: "OmniVPN-профили")
        }))
        return items
    }

    private func label(_ text: String) -> some View {
        Text(text).font(.system(size: 13, weight: .semibold)).foregroundColor(Theme.label).frame(width: 160, alignment: .leading)
    }

    private func field<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 10) {
            label(title)
            content().frame(width: 318)
            Spacer()
        }
        .padding(.leading, 40)
    }

    private func load(_ id: UUID?) {
        banner = nil
        if let id = id, let c = store.connections.first(where: { $0.id == id }) {
            editID = c.id
            name = c.name
            server = c.server
            psk = store.psk(c.id)
            routes = c.routes
            user = c.user
            group = c.groupName
            domain = c.domainName
            balanceIndex = c.isLatency ? 1 : 0
            dns = c.dnsServers.joined(separator: ", ")
            dnsDomain = c.dnsDomainName
        } else {
            editID = nil
            name = ""; server = ""; psk = ""; routes = ""; user = ""; group = ""; domain = ""; balanceIndex = 0; dns = ""; dnsDomain = ""
        }
    }

    private func save() {
        let n = name.trimmingCharacters(in: .whitespaces)
        let s = server.trimmingCharacters(in: .whitespaces)
        if n.isEmpty { banner = Banner(text: "Укажите название подключения.", isError: true); return }
        if store.connections.contains(where: { $0.name.lowercased() == n.lowercased() && $0.id != editID }) {
            banner = Banner(text: "Подключение с таким названием уже есть.", isError: true); return
        }
        if s.isEmpty { banner = Banner(text: "Укажите адрес VPN-сервера.", isError: true); return }
        if s.rangeOfCharacter(from: CharacterSet(charactersIn: " \t'\"`$;|&<>")) != nil {
            banner = Banner(text: "Адрес сервера содержит недопустимые символы.", isError: true); return
        }
        if psk.isEmpty { banner = Banner(text: "Укажите общий ключ IPsec (PSK).", isError: true); return }
        let parsed = RouteList.parse(routes)
        if let bad = parsed.bad { banner = Banner(text: "Неверная сеть «\(bad)». Пример: 192.168.88.0/24", isError: true); return }

        var c = store.connections.first(where: { $0.id == editID }) ?? VPNConnection()
        c.name = n
        c.server = s
        c.routes = parsed.routes.joined(separator: ", ")
        c.user = user.trimmingCharacters(in: .whitespaces)
        let g = group.trimmingCharacters(in: .whitespaces)
        c.group = g.isEmpty ? nil : g
        let d = domain.trimmingCharacters(in: .whitespaces)
        if let error = ProfileFile.checkDomain(d) { banner = Banner(text: "Домен: " + error, isError: true); return }
        c.domain = d.isEmpty ? nil : d
        let parsedDns = ProfileFile.parseDns(dns)
        let dd = dnsDomain.trimmingCharacters(in: .whitespaces)
        if let error = parsedDns.error ?? ProfileFile.checkDnsDomain(dd) { banner = Banner(text: "DNS: " + error, isError: true); return }
        c.dns = parsedDns.servers.isEmpty ? nil : parsedDns.servers.joined(separator: ", ")
        c.dnsDomain = dd.isEmpty ? nil : dd
        c.balance = balanceIndex == 1 ? "latency" : nil
        // Режим выбора канала и домен — общие для всей группы.
        if !g.isEmpty {
            for var m in store.connections where m.id != c.id && m.groupName.lowercased() == g.lowercased() {
                m.balance = c.balance
                if !d.isEmpty { m.domain = c.domain }
                // DNS офиса один на все каналы группы.
                if c.dns != nil { m.dns = c.dns }
                if c.dnsDomain != nil { m.dnsDomain = c.dnsDomain }
                store.upsert(m)
            }
        }
        store.upsert(c)
        store.setPsk(psk, c.id)
        store.selectedID = c.id
        editID = c.id
        vpn.banner = Banner(text: "Подключение «\(c.name)» сохранено.", isError: false)
        nav.page = 0
    }

    private func delete() {
        guard let id = editID else { return }
        if vpn.phase != .idle, vpn.active?.id == id {
            banner = Banner(text: "Это подключение сейчас используется. Сначала отключитесь.", isError: true)
            return
        }
        store.remove(id)
        load(store.selected?.id)
    }
}

// ---------- О программе ----------

struct AboutView: View {
    @EnvironmentObject var vpn: VPNController
    @EnvironmentObject var updater: Updater
    @State private var banner: Banner?

    var body: some View {
        VStack(spacing: 0) {
            PageTitle(text: "О программе")
            HStack(spacing: 16) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 84, height: 84)
                VStack(alignment: .leading, spacing: 4) {
                    Text("OmniVPN Connect").font(.system(size: 21, weight: .semibold)).foregroundColor(Theme.text)
                    Text("Версия \(appVersion)  ·  клиент L2TP/IPsec VPN для macOS")
                        .font(.system(size: 13)).foregroundColor(Theme.muted)
                }
                Spacer()
            }
            .padding(.leading, 34)
            .padding(.top, 12)
            VStack(spacing: 6) {
                row("Протокол", "L2TP/IPsec с общим ключом (PSK)")
                row("VPN-клиент", "встроенный в macOS")
                row("Весь трафик через VPN", "выключено: интернет идёт напрямую")
                row("Хранение данных", "пароль и ключ — в связке ключей macOS")
                row("Системная служба", SystemVPN.current() != nil ? "«\(SystemVPN.serviceName)» создана" : "ещё не создана")
            }
            .padding(.top, 18)
            updates
                .padding(.top, 18)
            if let b = banner {
                MessageBar(banner: b).frame(width: 488).padding(.top, 10)
            }
            Spacer()
            Button("Удалить VPN-подключение из системы", action: removeService)
                .buttonStyle(LinkButtonStyle(color: Theme.err))
                .disabled(vpn.phase != .idle || SystemVPN.current() == nil)
                .padding(.bottom, 24)
        }
    }

    // Обновления: последний релиз на GitHub.
    private var updates: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text("Обновления").font(.system(size: 13.5, weight: .semibold)).foregroundColor(Theme.text)
            Text(updater.status).font(.system(size: 13)).foregroundColor(updater.statusColor).lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
            if !updater.notes.isEmpty {
                Text(updater.notes).font(.system(size: 11)).foregroundColor(Theme.muted).lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if updater.phase == .downloading || updater.phase == .installing {
                ZStack(alignment: .leading) {
                    Rectangle().fill(Color(hex: 0xE3EAF1))
                    Rectangle().fill(Theme.primary).frame(width: 488 * updater.progress)
                }
                .frame(width: 488, height: 6)
                .padding(.top, 4)
            }
            HStack(spacing: 10) {
                Button("Проверить обновления") { updater.check(manual: true) }
                    .buttonStyle(PrimaryButtonStyle(secondary: true, width: 200))
                    .disabled(updater.phase != .idle)
                if let u = updater.available {
                    Button(updater.phase == .downloading ? "Отменить" : "Обновить до \(u.version)") {
                        if updater.phase == .downloading { updater.cancel() } else { updater.download() }
                    }
                    .buttonStyle(PrimaryButtonStyle(width: 200))
                    .disabled(updater.phase == .checking || updater.phase == .installing)
                }
            }
            .padding(.top, 6)
        }
        .frame(width: 488, alignment: .leading)
    }

    private func row(_ label: String, _ value: String) -> some View {
        HStack(spacing: 14) {
            Text(label).foregroundColor(Theme.label).frame(width: 190, alignment: .trailing)
            Text(value).foregroundColor(Theme.text)
            Spacer()
        }
        .font(.system(size: 13.5))
        .padding(.leading, 14)
    }

    private func removeService() {
        do {
            try SystemVPN.remove()
            banner = Banner(text: "VPN-подключение удалено из системных настроек. Оно будет создано заново при следующем подключении.", isError: false)
        } catch SystemVPN.Failure.cancelled {
        } catch {
            banner = Banner(text: error.localizedDescription, isError: true)
        }
    }
}
