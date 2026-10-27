import Foundation

// Общие логин и пароль в группе: откуда берутся данные для канала (без записи в связку ключей).
let store = Store(load: false)
let vpn = VPNController()
let channels = Channels(store: store, vpn: vpn)
let main = VPNConnection(name: "Офис — основной", server: "a", user: "user", savePassword: true, group: "Офис")
let res = VPNConnection(name: "Офис — резерв", server: "b", group: "Офис")
let other = VPNConnection(name: "Офис — чужой логин", server: "c", user: "petrov", group: "Офис")
let solo = VPNConnection(name: "Склад", server: "d")
store.connections = [main, res, other, solo]
for c in [res, other, solo] {
    let src = channels.credentialSource(c)
    print("  \(c.name): данные от «\(src.name)», login='\(c.user.isEmpty ? src.user : c.user)', save=\(src.savePassword)")
}
