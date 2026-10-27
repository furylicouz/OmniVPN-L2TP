# OmniVPN Connect

Клиент L2TP/IPsec VPN для **Windows** и **macOS** (Apple Silicon и Intel). Работает через встроенный
VPN-клиент системы, а программа даёт удобный интерфейс и то, чего в системе нет:

- подключение по адресу сервера, общему ключу (PSK), логину и паролю; данные можно сохранить
  (Windows — DPAPI, macOS — «Связка ключей»);
- интернет всегда идёт напрямую: «основной шлюз в удалённой сети» выключен, в VPN уходят только нужные сети;
- статус, время, трафик и график скорости подключения;
- профили `.omnivpn`: двойной щелчок по файлу добавляет подключения, как `.ovpn` у OpenVPN;
- несколько каналов одного офиса (группа): доступность каждого канала, подключение по порядку
  или туда, где меньше задержка, общий логин на группу;
- DNS офиса через VPN и маршруты к сетям офиса;
- вкладка «Удалённые ПК»: подключение по RDP в один щелчок, коллекции RDS, шлюз RD Gateway, домен в логине;
- индикатор интернета и задержки до VPN-сервера;
- самообновление из релизов этого репозитория.

## Скачать

Последняя версия — на странице **[Releases](https://github.com/furylicouz/OmniVPN-L2TP/releases/latest)**:

| Система | Файл |
|---|---|
| Windows 10/11 | `OmniVPNConnect-Setup-<версия>.exe` |
| macOS 12 и новее | `OmniVPNConnect-<версия>.dmg` |

**macOS, первый запуск.** Программа не проходила нотаризацию Apple, поэтому после загрузки из браузера macOS её
блокирует. Откройте «Системные настройки» → «Конфиденциальность и безопасность» → «Всё равно открыть»,
либо выполните в «Терминале»: `xattr -dr com.apple.quarantine "/Applications/OmniVPN Connect.app"`.

## Обновление

Установленная программа (начиная с версии 1.9.0) сама проверяет новые релизы при запуске и раз в 6 часов.
Когда выходит новая версия, в боковой панели появляется «Доступна версия …», а в разделе «О программе» —
кнопка «Обновить»: программа скачивает установщик с прогрессом, сверяет контрольную сумму SHA-256,
устанавливает и перезапускается. VPN-подключение при этом не разрывается.

Программы находят обновление по файлу `omnivpn-update.txt` в последнем релизе:

```
version = 1.9.1
notes = Что нового (по строке на пункт)
windows = OmniVPNConnect-Setup-1.9.1.exe
windows.size = <байт>
windows.sha256 = <sha256>
mac = OmniVPNConnect-1.9.1.dmg
mac.size = <байт>
mac.sha256 = <sha256>
```

## Файл профилей `.omnivpn`

Текст в духе INI: по секции `[Название]` на VPN-подключение или компьютер для удалённого рабочего стола.

```ini
# Строки, начинающиеся с # или ;, — комментарии.
[Офис — основной]
server = vpn.example.ru
psk = общий-ключ
# Сети офиса, которые идут через VPN
routes = 192.168.10.0/24, 192.168.20.0/24
# Каналы одного офиса; порядок в файле — приоритет. balance = задержка — подключать туда, где быстрее
group = Офис
balance = задержка
# Домен для логина на удалённых ПК: «user» превратится в «CORP\user»
domain = CORP
# DNS офиса (не больше двух) и домен, имена в котором ищутся через него
dns = 192.168.10.11, 192.168.10.12
dnsdomain = corp.local
# Логин; пароль при экспорте не пишется
user = user

[Офис — резерв]
server = vpn2.example.ru
psk = общий-ключ
group = Офис

[Терминальный сервер]
type = rdp
host = rds.corp.local
# Без этого VPN компьютер недоступен
vpn = Офис
# Коллекция RDS (тогда host — брокер) и шлюз RD Gateway
collection = Office
gateway = gw.example.ru
fullscreen = да
multimon = нет
```

Все поля, кроме `server` и `psk` (для VPN) и `host` (для компьютера), необязательны. Полное описание —
в начале `windows/src/ProfileFile.cs`.

## Сборка

### Windows (`windows/`)

Нужны Windows 10/11 с .NET Framework 4.8 (компилятор `csc.exe` входит в систему) и
[NSIS 3.10](https://nsis.sourceforge.io/Download) в виде zip-архива, распакованного в `windows\tools\nsis-3.10`.

```bat
cd windows
build.cmd
```

Результат: `windows\out\OmniVPNConnect.exe` и установщик `windows\out\OmniVPNConnect-Setup-<версия>.exe`.

Тестовый стенд (снимки экранов, разбор профилей, обновление и т. д.):

```bat
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:exe /main:L2TPConnect.TestMain ^
  /win32manifest:src\app.manifest /out:test\L2TPTest.exe /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.Security.dll src\Program.cs src\Ras.cs src\VpnProfile.cs src\AppSettings.cs ^
  src\ProfileFile.cs src\IkeProbe.cs src\NetCheck.cs src\Rdp.cs src\Updater.cs src\Theme.cs src\Widgets.cs ^
  src\Pages.cs src\MainForm.cs test\TestMain.cs
test\L2TPTest.exe units
test\L2TPTest.exe profiles test\sample.omnivpn
```

### macOS (`macos/`)

Нужны Xcode Command Line Tools (`xcode-select --install`).

```sh
cd macos
./build.sh                  # build/OmniVPN Connect.app и build/OmniVPNConnect-<версия>.dmg
bash test/build-tests.sh    # тестовые программы в build/: profiles, rdpfile, update, snapshot…
./build/profiles test/sample.omnivpn
```

Программа собирается универсальной (arm64 + x86_64) и подписывается ad-hoc. Чтобы подписать своим
сертификатом: `SIGN_IDENTITY="Developer ID Application: …" ./build.sh`.

## Выпуск новой версии

1. Поднять версию в `windows/src/Program.cs`, `windows/src/installer.nsi` и `macos/Info.plist`
   (там же увеличить `CFBundleVersion`).
2. Собрать на Windows (`build.cmd`) и скопировать `OmniVPNConnect-Setup-<версия>.exe` в `macos/release/`.
3. Собрать на Mac (`./build.sh`) и описать изменения в `macos/release/notes-<версия>.txt`, по пункту в строке.
4. Войти в GitHub (`gh auth login`) или положить токен с правом Contents: Read and write
   в `~/.config/omnivpn/github-token` и выполнить:

   ```sh
   cd macos
   DRY_RUN=1 release/publish.sh 1.9.1 release/notes-1.9.1.txt   # посмотреть, что уйдёт
   release/publish.sh 1.9.1 release/notes-1.9.1.txt
   ```

Скрипт создаёт черновик релиза, загружает установщики и `omnivpn-update.txt` и только потом публикует релиз —
программы не увидят его, пока в нём нет всех файлов.

## Лицензия

[MIT](LICENSE)
