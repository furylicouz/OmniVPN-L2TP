import Foundation

// Проверка доступности серверов тем же кодом, что в программе: probe host1 host2 ...
for host in CommandLine.arguments.dropFirst() {
    let r = IkeProbe.checkSync(host)
    print("  \(host.padding(toLength: 22, withPad: " ", startingAt: 0)) \(r.state) \(r.rttMs) мс\(r.nameNotFound ? " (имя не найдено)" : "")")
}
