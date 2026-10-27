import AppKit
import CryptoKit
import Foundation
import SwiftUI

// Последний релиз на GitHub: файл omnivpn-update.txt рядом с установщиками.
struct UpdateInfo: Equatable {
    var version = ""
    var notes = ""
    var file = ""       // образ диска для macOS
    var sha256 = ""
    var size: Int64 = 0

    var url: URL {
        let name = file.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? file
        return URL(string: Updater.releaseBase + "v" + version + "/" + name)!
    }

    // Строки «ключ = значение»; notes может повторяться (по строке на пункт).
    static func parse(_ text: String, platform: String) throws -> UpdateInfo {
        var u = UpdateInfo()
        var notes: [String] = []
        for raw in text.replacingOccurrences(of: "\r", with: "").components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, !line.hasPrefix("#"), let eq = line.firstIndex(of: "="), eq != line.startIndex else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            switch key {
            case "version": u.version = value.hasPrefix("v") || value.hasPrefix("V") ? String(value.dropFirst()) : value
            case "notes": notes.append(value)
            case platform: u.file = value
            case platform + ".sha256": u.sha256 = value.lowercased()
            case platform + ".size": u.size = Int64(value) ?? 0
            default: break
            }
        }
        u.notes = notes.joined(separator: "\n")
        guard Updater.components(u.version) != nil else { throw Updater.Failure("в описании релиза нет номера версии") }
        guard !u.file.isEmpty, !u.file.contains("/"), u.sha256.count == 64, u.size > 0 else {
            throw Updater.Failure("в релизе \(u.version) нет программы для macOS")
        }
        return u
    }
}

final class Updater: NSObject, ObservableObject, URLSessionDownloadDelegate {
    static let repo = "furylicouz/OmniVPN-L2TP"
    static let manifestName = "omnivpn-update.txt"
    static let releaseBase = "https://github.com/\(repo)/releases/download/"
    static let manifestURL = URL(string: "https://github.com/\(repo)/releases/latest/download/\(manifestName)")!

    struct Failure: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }

    enum Phase { case idle, checking, downloading, installing }

    @Published private(set) var phase = Phase.idle
    @Published private(set) var available: UpdateInfo?
    @Published private(set) var progress: Double = 0
    @Published private(set) var status = "Ещё не проверялись"
    @Published private(set) var statusColor = Theme.label

    // Выход без вопроса про VPN (его задаёт AppDelegate).
    var quitForUpdate: () -> Void = { NSApp.terminate(nil) }

    private var timer: Timer?
    private var session: URLSession?
    private var downloading: UpdateInfo?
    private var shownPercent = -1

    static var workDir: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("OmniVPNConnect-update", isDirectory: true)
    }

    // Первая проверка через 5 с после запуска, дальше раз в 6 часов.
    func start() {
        // Образ и распакованная копия от прошлого обновления больше не нужны.
        try? FileManager.default.removeItem(at: Updater.workDir)
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in self?.check(manual: false) }
        let t = Timer(timeInterval: 6 * 3600, repeats: true) { [weak self] _ in self?.check(manual: false) }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func check(manual: Bool) {
        guard phase == .idle else { return }
        phase = .checking
        if manual { setStatus("Проверка обновлений…", Theme.label) }
        var req = URLRequest(url: Updater.manifestURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        req.setValue("OmniVPNConnect/\(appVersion)", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, response, error in
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            let result: Result<UpdateInfo, Error>
            if let error = error {
                result = .failure(Failure(Updater.explain(error)))
            } else if code == 404 {
                result = .failure(Failure("на GitHub ещё нет опубликованных релизов"))
            } else if code != 200 {
                result = .failure(Failure("github.com ответил ошибкой \(code)"))
            } else {
                result = Result { try UpdateInfo.parse(String(decoding: data ?? Data(), as: UTF8.self), platform: "mac") }
            }
            DispatchQueue.main.async { self.finishCheck(result, manual: manual) }
        }.resume()
    }

    private func finishCheck(_ result: Result<UpdateInfo, Error>, manual: Bool) {
        phase = .idle
        switch result {
        case .success(let u) where Updater.isNewer(u.version, than: appVersion):
            available = u
            setStatus("Доступна версия \(u.version) (у вас \(appVersion))", Theme.primary)
        case .success:
            available = nil
            setStatus("Установлена последняя версия (\(appVersion))", Theme.ok)
        case .failure(let error):
            // Автоматическая проверка без интернета молчит; ошибку показываем, только если нажали «Проверить».
            if manual { setStatus("Не удалось проверить: " + error.localizedDescription, Theme.err) }
        }
    }

    var notes: String {
        guard let u = available else { return "" }
        return (u.notes.isEmpty ? "" : u.notes + "\n") + "Размер \(Updater.mb(u.size)). VPN во время обновления не отключается."
    }

    // ---------- Загрузка ----------

    func download() {
        guard phase == .idle, let u = available else { return }
        if let problem = Updater.installProblem() {
            setStatus(problem, Theme.busy)
            return
        }
        phase = .downloading
        progress = 0
        shownPercent = -1
        downloading = u
        setStatus("Загрузка версии \(u.version)…", Theme.label)
        let s = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: .main)
        session = s
        var req = URLRequest(url: u.url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        req.setValue("OmniVPNConnect/\(appVersion)", forHTTPHeaderField: "User-Agent")
        s.downloadTask(with: req).resume()
    }

    func cancel() {
        guard phase == .downloading else { return }
        session?.invalidateAndCancel()
        session = nil
        phase = .idle
        setStatus("Загрузка отменена", Theme.label)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard let u = downloading, session === self.session else { return }
        let total = max(u.size, 1)
        progress = min(1, Double(totalBytesWritten) / Double(total))
        let pct = Int(progress * 100)
        if pct != shownPercent {
            shownPercent = pct
            setStatus("Загрузка версии \(u.version): \(pct)%  ·  \(Updater.mb(totalBytesWritten)) из \(Updater.mb(total))", Theme.label)
        }
        if totalBytesWritten > u.size { downloadTask.cancel() }
    }

    // Файл нужно забрать до выхода из этого метода — потом URLSession его удалит.
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let u = downloading, session === self.session else { return }
        let code = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        let dir = Updater.workDir
        let dmg = dir.appendingPathComponent(u.file)
        do {
            guard code == 200 else { throw Failure("github.com ответил ошибкой \(code)") }
            try Updater.verify(location, u)
            try? FileManager.default.removeItem(at: dir)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try FileManager.default.moveItem(at: location, to: dmg)
        } catch {
            fail(error)
            return
        }
        self.session = nil
        session.finishTasksAndInvalidate()
        progress = 1
        phase = .installing
        setStatus("Установка версии \(u.version)…", Theme.label)
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Result {
                try Updater.install(dmg, version: u.version)
                try Updater.relaunch(Bundle.main.bundleURL)
            }
            DispatchQueue.main.async {
                switch result {
                case .success: self.quitForUpdate()
                case .failure(let error): self.fail(error)
                }
            }
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error = error, session === self.session else { return }
        self.session = nil
        fail(Failure(Updater.explain(error)))
    }

    private func fail(_ error: Error) {
        session?.invalidateAndCancel()
        session = nil
        phase = .idle
        if case SystemVPN.Failure.cancelled = error {
            setStatus("Установка отменена: нужен пароль администратора", Theme.busy)
        } else {
            setStatus("Не удалось обновить: " + error.localizedDescription, Theme.err)
        }
    }

    private func setStatus(_ text: String, _ color: Color) {
        status = text
        statusColor = color
    }

    // ---------- Установка ----------

    // Обновить можно только программу, запущенную из постоянного места (обычно «Программы»).
    static func installProblem() -> String? {
        let path = Bundle.main.bundlePath
        if path.contains("/AppTranslocation/") || path.hasPrefix("/Volumes/") {
            return "Перенесите OmniVPN Connect в папку «Программы» и запустите оттуда — тогда обновление установится"
        }
        return nil
    }

    static func verify(_ file: URL, _ u: UpdateInfo) throws {
        let data = try Data(contentsOf: file)
        guard Int64(data.count) == u.size else { throw Failure("файл загрузился не полностью") }
        let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard hash == u.sha256 else { throw Failure("контрольная сумма не совпала") }
    }

    // Достаёт программу из образа и заменяет ею target (по умолчанию — запущенную).
    static func install(_ dmg: URL, version: String, target: URL = Bundle.main.bundleURL) throws {
        let work = dmg.deletingLastPathComponent()
        let mount = work.appendingPathComponent("mnt", isDirectory: true)
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: true)
        try run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-noautoopen", "-readonly", "-quiet", "-mountpoint", mount.path, dmg.path])
        let staged: URL
        do {
            defer { _ = try? run("/usr/bin/hdiutil", ["detach", "-quiet", "-force", mount.path]) }
            guard let app = try FileManager.default.contentsOfDirectory(at: mount, includingPropertiesForKeys: nil)
                .first(where: { $0.pathExtension == "app" }) else { throw Failure("в образе нет программы") }
            let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist"))
            guard info?["CFBundleIdentifier"] as? String == (Bundle.main.bundleIdentifier ?? "app.omnivpn.connect"),
                  info?["CFBundleShortVersionString"] as? String == version else {
                throw Failure("в образе другая программа или версия")
            }
            staged = work.appendingPathComponent(app.lastPathComponent)
            try run("/usr/bin/ditto", [app.path, staged.path])
        }
        try run("/usr/bin/codesign", ["--verify", "--deep", staged.path])

        let parent = target.deletingLastPathComponent().path
        if FileManager.default.isWritableFile(atPath: parent) && FileManager.default.isWritableFile(atPath: target.path) {
            try replace(target.path, with: staged.path)
        } else {
            try SystemVPN.runPrivileged(["--install-update", target.path, staged.path],
                                        prompt: "OmniVPN Connect устанавливает обновление до версии \(version).")
        }
    }

    // Новая версия запустится, когда эта завершится. VPN-подключение системное и не прерывается.
    static func relaunch(_ target: URL) throws {
        let relaunch = Process()
        relaunch.executableURL = URL(fileURLWithPath: "/bin/sh")
        relaunch.arguments = ["-c", "while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"", target.path]
        try relaunch.run()
    }

    // Подмена через переименование рядом с программой: старая версия удаляется, только когда новая на месте.
    static func replace(_ target: String, with staged: String) throws {
        let script = """
        set -e
        T="$1"; S="$2"; P=$(dirname "$T"); N="$P/.omnivpn-update.app"; O="$P/.omnivpn-old.app"
        rm -rf "$N" "$O"
        /usr/bin/ditto "$S" "$N"
        /usr/bin/xattr -dr com.apple.quarantine "$N" 2>/dev/null || true
        mv "$T" "$O"
        if ! mv "$N" "$T"; then mv "$O" "$T"; exit 1; fi
        rm -rf "$O"
        """
        try run("/bin/sh", ["-c", script, "sh", target, staged])
    }

    @discardableResult
    static func run(_ tool: String, _ args: [String]) throws -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let out = Pipe()
        p.standardOutput = out
        p.standardError = out
        try p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        let text = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        guard p.terminationStatus == 0 else {
            throw Failure("\((tool as NSString).lastPathComponent): " + (text.isEmpty ? "код \(p.terminationStatus)" : text))
        }
        return text
    }

    // ---------- Версии ----------

    static func components(_ v: String) -> [Int]? {
        let parts = v.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard !parts.isEmpty, parts.count <= 4, !parts.contains(where: { $0 == nil || $0! < 0 }) else { return nil }
        return parts.map { $0! }
    }

    static func isNewer(_ latest: String, than current: String) -> Bool {
        guard var a = components(latest), var b = components(current) else { return false }
        while a.count < 4 { a.append(0) }
        while b.count < 4 { b.append(0) }
        return a.lexicographicallyPrecedes(b) == false && a != b
    }

    static func mb(_ bytes: Int64) -> String {
        String(format: "%.1f МБ", Double(bytes) / 1_048_576).replacingOccurrences(of: ".", with: ",")
    }

    static func explain(_ error: Error) -> String {
        let code = (error as NSError).code
        switch code {
        case NSURLErrorCannotFindHost, NSURLErrorDNSLookupFailed: return "нет доступа к github.com (имя не найдено)"
        case NSURLErrorTimedOut: return "github.com не ответил вовремя"
        case NSURLErrorCancelled: return "загрузка прервана"
        default: return "нет связи с github.com"
        }
    }

#if SNAPSHOT
    func testSet(_ phase: Phase, available: UpdateInfo?, progress: Double = 0, status: String, color: Color) {
        self.phase = phase
        self.available = available
        self.progress = progress
        setStatus(status, color)
    }
#endif
}
