import AppKit

// Root-операции (вызываются через запрос пароля администратора) выполняются без интерфейса.
if let code = CLI.run(CommandLine.arguments) {
    exit(code)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
