import AppKit

// Каким приложением macOS откроет .rdp, который пишет программа (сам файл не открывается).
let h = RdpHost(name: "Проверка", host: "192.168.20.25:3390", user: "CORP\\user", fullScreen: false, multiMon: true)
let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("OmniVPNConnect/rdp-test")
try! FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
let url = dir.appendingPathComponent("check.rdp")
try! "full address:s:\(h.host)\r\nusername:s:\(h.user)\r\n".write(to: url, atomically: true, encoding: .utf8)
print("handler:", NSWorkspace.shared.urlForApplication(toOpen: url)?.path ?? "нет приложения")
try? FileManager.default.removeItem(at: dir)
