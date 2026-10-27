import AppKit
import Combine
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let store = Store()
    let vpn = VPNController()
    let nav = Navigation()
    lazy var profiles = ProfileIO(store: store, vpn: vpn, nav: nav)
    lazy var channels = Channels(store: store, vpn: vpn)
    lazy var rdp = RdpCenter(store: store, vpn: vpn, channels: channels, nav: nav)
    lazy var net = NetMonitor(store: store, vpn: vpn, channels: channels)
    let updater = Updater()
    private var launched = false
    private var pendingFiles: [URL] = []
    private var window: NSWindow!
    private var statusItem: NSStatusItem!
    private var cancellables = Set<AnyCancellable>()
    private var quitting = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMainMenu()

        let root = MainView().environmentObject(store).environmentObject(vpn).environmentObject(nav).environmentObject(profiles).environmentObject(channels).environmentObject(rdp).environmentObject(net).environmentObject(updater)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 540),
                          styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "OmniVPN Connect"
        window.contentView = NSHostingView(rootView: root)
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        window.setFrameAutosaveName("MainWindow")
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        vpn.$phase.combineLatest(store.$connections)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateStatusItem() }
            .store(in: &cancellables)
        updater.$available.dropFirst().receive(on: RunLoop.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.updateStatusItem() } }
            .store(in: &cancellables)

        vpn.adoptExisting(store: store)
        channels.probeAll(force: true)
        net.check()
        updater.quitForUpdate = { [weak self] in
            self?.quitting = true
            NSApp.terminate(nil)
        }
        updater.start()
        store.$connections.dropFirst().receive(on: RunLoop.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.channels.probeAll(force: false) } }
            .store(in: &cancellables)

        launched = true
        if !pendingFiles.isEmpty {
            let files = pendingFiles
            pendingFiles = []
            DispatchQueue.main.async { files.forEach(self.profiles.importFile) }
        }
    }

    // Двойной щелчок по файлу .omnivpn в Finder. При холодном старте приходит до didFinishLaunching.
    func application(_ application: NSApplication, open urls: [URL]) {
        let files = urls.filter { $0.pathExtension.lowercased() == ProfileFile.fileExtension }
        guard launched else {
            pendingFiles += files
            return
        }
        showWindow()
        files.forEach(profiles.importFile)
    }

    // ---------- Меню приложения (нужно и для Cmd+C / Cmd+V в полях) ----------

    private func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "О программе OmniVPN Connect", action: #selector(showAbout), keyEquivalent: "").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Скрыть OmniVPN Connect", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Завершить OmniVPN Connect", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Правка")
        edit.addItem(withTitle: "Отменить", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Повторить", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Вырезать", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Копировать", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Вставить", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Выбрать все", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let win = NSMenu(title: "Окно")
        win.addItem(withTitle: "Свернуть", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        win.addItem(withTitle: "Закрыть", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowItem.submenu = win
        main.addItem(windowItem)
        NSApp.mainMenu = main
        NSApp.windowsMenu = win
    }

    // ---------- Значок в строке меню ----------

    private func updateStatusItem() {
        let symbol: String
        let status: String
        switch vpn.phase {
        case .connected: symbol = "lock.shield.fill"; status = "Подключено"
        case .connecting: symbol = "arrow.triangle.2.circlepath"; status = "Подключение…"
        case .disconnecting: symbol = "arrow.triangle.2.circlepath"; status = "Отключение…"
        case .idle: symbol = "lock.shield"; status = "Не подключено"
        }
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "OmniVPN Connect")
        image?.isTemplate = true
        statusItem.button?.image = image
        statusItem.button?.toolTip = "OmniVPN Connect — " + status.lowercased()

        let menu = NSMenu()
        let header = NSMenuItem(title: "OmniVPN Connect: " + status.lowercased(), action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        if vpn.phase == .connected, let a = vpn.active {
            let detail = NSMenuItem(title: "«\(a.name)» · \(a.server)", action: nil, keyEquivalent: "")
            detail.isEnabled = false
            menu.addItem(detail)
        }
        menu.addItem(.separator())
        switch vpn.phase {
        case .connected:
            menu.addItem(withTitle: "Отключить VPN", action: #selector(toggleVPN), keyEquivalent: "").target = self
        case .idle:
            let item = menu.addItem(withTitle: "Подключить VPN", action: #selector(toggleVPN), keyEquivalent: "")
            item.target = self
            item.isEnabled = !store.connections.isEmpty
        default:
            menu.addItem(withTitle: "Отменить подключение", action: #selector(toggleVPN), keyEquivalent: "").target = self
        }
        menu.addItem(withTitle: "Открыть окно OmniVPN Connect", action: #selector(showWindow), keyEquivalent: "").target = self
        if let u = updater.available {
            menu.addItem(withTitle: "Обновить до версии \(u.version)…", action: #selector(showUpdate), keyEquivalent: "").target = self
        }
        menu.addItem(.separator())
        menu.addItem(withTitle: "Выход", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "")
        menu.autoenablesItems = false
        statusItem.menu = menu
    }

    @objc private func toggleVPN() {
        switch vpn.phase {
        case .idle:
            // Если пароль сохранён — подключаемся сразу, иначе открываем окно для ввода.
            if let c = store.selected, c.savePassword, !c.user.isEmpty {
                let password = store.password(c.id), psk = store.psk(c.id)
                if !password.isEmpty && !psk.isEmpty {
                    vpn.connect(c, user: c.user, password: password, psk: psk)
                    return
                }
            }
            nav.page = 0
            showWindow()
        default:
            vpn.disconnect()
        }
    }

    @objc func showWindow() {
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func showUpdate() {
        nav.page = 3
        showWindow()
    }

    @objc private func showAbout() {
        nav.page = 3
        showWindow()
    }

    // ---------- Закрытие и выход ----------

    // Крестик при активном VPN оставляет программу в строке меню; без VPN — выход, как в Windows-версии.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if vpn.phase == .idle {
            NSApp.terminate(nil)
            return false
        }
        window.orderOut(nil)
        return false
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWindow()
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard vpn.phase != .idle, !quitting else { return .terminateNow }
        showWindow()
        let alert = NSAlert()
        alert.messageText = "VPN подключён"
        alert.informativeText = "Отключить VPN перед выходом? Если оставить, подключение продолжит работать без программы."
        alert.addButton(withTitle: "Отключить и выйти")
        alert.addButton(withTitle: "Выйти, оставив VPN")
        alert.addButton(withTitle: "Отмена")
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            vpn.disconnect()
            quitting = true
            return .terminateNow
        case .alertSecondButtonReturn:
            quitting = true
            return .terminateNow
        default:
            return .terminateCancel
        }
    }
}
