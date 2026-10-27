import Foundation

// Логин и домен в .rdp: домен должен попасть в файл ровно один раз.
var failed = false
for user in ["user", "CORP\\user", "user@corp.local", ""] {
    var h = RdpHost()
    h.name = "domain-test"
    h.host = "rds.corp.local"
    h.user = user
    let lines = RdpLauncher.fileText(h, domain: "CORP").components(separatedBy: "\r\n")
        .filter { $0.hasPrefix("username:") || $0.hasPrefix("domain:") }
    print("  логин «\(user)»: " + lines.joined(separator: " | "))
    let text = lines.joined(separator: " ")
    if text.components(separatedBy: "CORP").count - 1 > 1 { failed = true }
}
print(failed ? "FAIL" : "OK")
exit(failed ? 1 : 0)
