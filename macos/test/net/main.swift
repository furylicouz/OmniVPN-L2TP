import Foundation

// Проверка интернета и ping тем же кодом, что в программе: net host1 host2 ...
let inet = NetMonitor.internet()
print("  интернет: \(inet.state) \(inet.rttMs) мс, DNS: \(NetMonitor.dnsWorks())")
for host in CommandLine.arguments.dropFirst() {
    let p = NetMonitor.ping(host)
    print("  ping \(host.padding(toLength: 22, withPad: " ", startingAt: 0)) \(p.state) \(p.rttMs) мс\(p.nameNotFound ? " (имя не найдено)" : "")")
}
