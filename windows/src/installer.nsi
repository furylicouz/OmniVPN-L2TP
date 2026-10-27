Unicode true
ManifestDPIAware true

!define APPNAME "OmniVPN Connect"
!define APPEXE "OmniVPNConnect.exe"
!define VERSION "1.9.1"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\OmniVPNConnect"
; Программа до переименования (версии 1.0–1.1).
!define LEGACYNAME "L2TP Connect"
!define LEGACYEXE "L2TPConnect.exe"
!define LEGACYKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\L2TPConnect"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

Name "${APPNAME}"
OutFile "..\out\OmniVPNConnect-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APPNAME}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "1.9.1.0"
VIAddVersionKey /LANG=1049 "ProductName" "${APPNAME}"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка ${APPNAME}"
VIAddVersionKey /LANG=1049 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" ""

!define MUI_ICON "..\out\app.ico"
!define MUI_UNICON "..\out\app.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_NODESC
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Function .onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
FunctionEnd

; Обновление из программы: «Setup.exe /S /RELAUNCH» ставит молча и запускает программу снова.
; Через explorer.exe — чтобы программа запустилась от имени пользователя, а не администратора.
Function .onInstSuccess
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/RELAUNCH" $1
  ${IfNot} ${Errors}
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APPEXE}"'
  ${EndIf}
FunctionEnd

Function un.onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
FunctionEnd

; Убирает файлы и ярлыки «L2TP Connect». Её деинсталлятор не запускаем: он удалил бы
; сохранённые подключения, а новая версия переносит их в свою папку при первом запуске.
Function RemoveLegacy
  ReadRegStr $0 HKLM "${LEGACYKEY}" "InstallLocation"
  ${If} $0 != ""
    nsExec::Exec 'taskkill /IM ${LEGACYEXE} /F'
    Pop $1
    Delete "$0\${LEGACYEXE}"
    Delete "$0\uninstall.exe"
    RMDir "$0"
    SetShellVarContext all
    Delete "$SMPROGRAMS\${LEGACYNAME}.lnk"
    Delete "$DESKTOP\${LEGACYNAME}.lnk"
    DeleteRegKey HKLM "${LEGACYKEY}"
  ${EndIf}
FunctionEnd

Section "!${APPNAME}" SecMain
  SectionIn RO
  nsExec::Exec 'taskkill /IM ${APPEXE} /F'
  Pop $0
  Call RemoveLegacy
  SetOutPath "$INSTDIR"
  File "..\out\${APPEXE}"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  SetShellVarContext all
  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"

  WriteRegStr HKLM "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${APPEXE}"
  WriteRegStr HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "EstimatedSize" 200

  ; Файлы профилей .omnivpn открываются программой — импорт подключений, как .ovpn у OpenVPN.
  WriteRegStr HKCR ".omnivpn" "" "OmniVPNConnect.Profile"
  WriteRegStr HKCR "OmniVPNConnect.Profile" "" "Профиль OmniVPN Connect"
  WriteRegStr HKCR "OmniVPNConnect.Profile\DefaultIcon" "" "$INSTDIR\${APPEXE},0"
  WriteRegStr HKCR "OmniVPNConnect.Profile\shell\open\command" "" '"$INSTDIR\${APPEXE}" "%1"'
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd

; L2TP/IPsec к серверу за NAT: без этого ключа Windows не подключается (ошибка 809).
Section "Поддержка VPN-сервера за NAT (может потребоваться перезагрузка)" SecNat
  ReadRegDWORD $0 HKLM "SYSTEM\CurrentControlSet\Services\PolicyAgent" "AssumeUDPEncapsulationContextOnSendRule"
  ${If} $0 != 2
    WriteRegDWORD HKLM "SYSTEM\CurrentControlSet\Services\PolicyAgent" "AssumeUDPEncapsulationContextOnSendRule" 2
    SetRebootFlag true
  ${EndIf}
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /IM ${APPEXE} /F'
  Pop $0
  ExecWait '"$INSTDIR\${APPEXE}" --cleanup'

  Delete "$INSTDIR\${APPEXE}"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"

  SetShellVarContext all
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"

  DeleteRegKey HKLM "${UNINSTKEY}"

  ReadRegStr $0 HKCR ".omnivpn" ""
  ${If} $0 == "OmniVPNConnect.Profile"
    DeleteRegKey HKCR ".omnivpn"
  ${EndIf}
  DeleteRegKey HKCR "OmniVPNConnect.Profile"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
