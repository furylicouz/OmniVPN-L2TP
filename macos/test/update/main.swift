import Foundation

// Описание релиза, сравнение версий; с аргументами «check [версия] [download]» — настоящий релиз на GitHub.
let args = CommandLine.arguments
var failed = false
func expect(_ ok: Bool, _ what: String) { print((ok ? "  ok   " : "  FAIL ") + what); if !ok { failed = true } }

let sample = "# релиз\nversion = v1.10.0\nnotes = Первое\nnotes = Второе\nwindows = OmniVPNConnect-Setup-1.10.0.exe\n" +
    "mac = OmniVPNConnect-1.10.0.dmg\nmac.size = 1672743\nmac.sha256 = " + String(repeating: "A", count: 64) + "\n"
let u = try! UpdateInfo.parse(sample, platform: "mac")
expect(u.version == "1.10.0" && u.file == "OmniVPNConnect-1.10.0.dmg" && u.size == 1672743 && u.notes == "Первое\nВторое", "разбор: \(u.version) \(u.file)")
expect(u.url.absoluteString == "https://github.com/furylicouz/OmniVPN-L2TP/releases/download/v1.10.0/OmniVPNConnect-1.10.0.dmg", u.url.absoluteString)
expect(Updater.isNewer("1.10.0", than: "1.9.0") && Updater.isNewer("1.9.1", than: "1.9.0"), "1.10.0 и 1.9.1 новее 1.9.0")
expect(!Updater.isNewer("1.9", than: "1.9.0") && !Updater.isNewer("1.8.9", than: "1.9.0") && !Updater.isNewer("мусор", than: "1.9.0"), "1.9, 1.8.9, мусор — не новее")
for bad in ["version = 1.9.0\n", "mac = a.dmg\nmac.size=1\nmac.sha256=" + String(repeating: "a", count: 64),
            "version=1.9.0\nmac=../x.dmg\nmac.size=1\nmac.sha256=" + String(repeating: "a", count: 64)] {
    expect((try? UpdateInfo.parse(bad, platform: "mac")) == nil, "отклонён: " + bad.replacingOccurrences(of: "\n", with: " | ").prefix(50))
}

if args.count > 1 && args[1] == "check" {
    let current = args.count > 2 ? args[2] : appVersion
    let data = try! Data(contentsOf: Updater.manifestURL)
    let r = try! UpdateInfo.parse(String(decoding: data, as: UTF8.self), platform: "mac")
    print("  релиз \(r.version): \(r.file), \(r.size) байт, новее \(current): \(Updater.isNewer(r.version, than: current))")
    if args.count > 3 && args[3] == "download" {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(r.file)
        try? FileManager.default.removeItem(at: file)
        try! Data(contentsOf: r.url).write(to: file)
        do {
            try Updater.verify(file, r)
            print("  скачан и проверен: \(file.path)")
        } catch {
            print("  FAIL проверка: \(error.localizedDescription)"); failed = true
        }
        try? FileManager.default.removeItem(at: file)
    }
}
if args.count > 2 && args[1] == "manifest" {
    // manifest <файл>: то, что сгенерировал release/publish.sh, читается для обеих систем.
    let text = try! String(contentsOfFile: args[2], encoding: .utf8)
    for platform in ["mac", "windows"] {
        do {
            let m = try UpdateInfo.parse(text, platform: platform)
            print("  ok   \(platform): \(m.version) \(m.file) \(m.size) \(m.sha256.prefix(16)) notes=\(m.notes.components(separatedBy: "\n").count)")
        } catch {
            expect(false, "\(platform): \(error.localizedDescription)")
        }
    }
}
if args.count > 4 && args[1] == "install" {
    // install <dmg> <версия> <программа.app>: замена копии программы, без перезапуска.
    let target = URL(fileURLWithPath: args[4])
    do {
        // Как в программе: образ лежит в своей временной папке, рядом распаковывается копия.
        let dmg = target.deletingLastPathComponent().appendingPathComponent("work/" + (args[2] as NSString).lastPathComponent)
        try FileManager.default.createDirectory(at: dmg.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.copyItem(at: URL(fileURLWithPath: args[2]), to: dmg)
        try Updater.install(dmg, version: args[3], target: target)
        try FileManager.default.removeItem(at: dmg.deletingLastPathComponent())
        let v = NSDictionary(contentsOf: target.appendingPathComponent("Contents/Info.plist"))?["CFBundleShortVersionString"] as? String
        expect(v == args[3], "после замены версия \(v ?? "?")")
        let left = (try? FileManager.default.contentsOfDirectory(atPath: target.deletingLastPathComponent().path)) ?? []
        expect(!left.contains(where: { $0.hasPrefix(".omnivpn-") }), "временных копий не осталось: \(left)")
        try Updater.run("/usr/bin/codesign", ["--verify", "--deep", target.path])
        print("  ok   подпись новой версии в порядке")
    } catch {
        expect(false, "установка: \(error.localizedDescription)")
    }
}
print(failed ? "FAIL" : "OK")
exit(failed ? 1 : 0)
