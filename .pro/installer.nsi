; ============================================================================
;  风灵月影宗藏宝阁 · NSIS 安装程序（MUI2 标准向导界面）
;
;  用 NSIS 自带的现代界面（Modern UI 2），不再自绘：
;    欢迎 → 选择安装位置 → 选择快捷方式 → 安装进度 → 完成
;  · 没有许可协议页，也没有任何免责声明页
;  · 记住上次安装位置（InstallDirRegKey），下次安装 / 自动更新自动填回
;  · 完成后可直接勾选「立即运行」
;  · 支持静默安装 /S（所有页面自动跳过，直接装完退出）
;  · 卸载会把安装目录整个删干净（含 dbs\ f\ 等数据），不留残留
;
;  编译：makensis installer.nsi
;  变量（软件名 / 版本 / 产物目录 / 图标）由 publish.ps1 写进
;  installer-config.nsh；直接编译本脚本时用下面的默认值。
; ============================================================================

Unicode true

; ---------------------------------------------------------------------------
; 压缩设置
;   NSIS 默认是 zlib 且非 solid，对 .NET 单文件（大段 IL + 大量字符串）压不动。
;   换成 LZMA + solid 后同一份产物能小一半（273MB → 72.7MB，实测）。
;   · /SOLID：所有文件连成一个流压，字典跨文件共享
;   · DictSize：字典大小（MB），越大压得越小，但安装器解压时要占同样多的内存
; ---------------------------------------------------------------------------
!ifndef COMPRESS_DICT_MB
  !define COMPRESS_DICT_MB 64
!endif
SetCompressor /SOLID lzma
SetCompressorDictSize ${COMPRESS_DICT_MB}
SetDatablockOptimize on
CRCCheck on

; ---------------------------------------------------------------------------
; 构建参数（publish.ps1 生成）
; ---------------------------------------------------------------------------
!if /FILEEXISTS "${__FILEDIR__}\installer-config.nsh"
  !include "installer-config.nsh"
!endif

!ifndef APP_NAME
  !define APP_NAME "风灵月影宗藏宝阁"
!endif
; 安装目录名（$LOCALAPPDATA\Programs\ 下面那一层）：故意用英文。
; 中文目录在命令行、某些杀软白名单、以及用户手动敲路径时都容易出问题，
; 而窗口标题 / 快捷方式 / 注册表显示名仍然走 APP_NAME，界面上还是中文。
!ifndef APP_DIR
  !define APP_DIR "FlingTrainerX"
!endif
!ifndef APP_VERSION
  !define APP_VERSION "1.0.0"
!endif
!ifndef APP_VERSION4
  !define APP_VERSION4 "1.0.0.0"
!endif
!ifndef APP_PUBLISHER
  !define APP_PUBLISHER "风灵月影宗"
!endif
!ifndef APP_ID
  !define APP_ID "{6FBA2F09-30A8-4EB7-B6CB-B42A0FB7F90D}"
!endif
!ifndef APP_REG_KEY
  !define APP_REG_KEY "Software\${APP_NAME}"
!endif
!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\..\FlingTrainerX\bin\Release\net10.0-windows\win-x64\publish"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "."
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "${APP_NAME}-${APP_VERSION}"
!endif
!ifndef APP_ICON
  !define APP_ICON "..\FlingTrainerX\Assets\app.ico"
!endif
!ifndef LEGACY_EXE
  !define LEGACY_EXE "风灵月影宗储物袋.exe"
!endif

!define APP_EXE "${APP_NAME}.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define APP_MUTEX "${APP_ID}"

; ---------------------------------------------------------------------------
; 安装包基本信息
; ---------------------------------------------------------------------------
Name "${APP_NAME} ${APP_VERSION}"
Caption "${APP_NAME} ${APP_VERSION} 安装程序"
OutFile "${OUTPUT_DIR}\${OUTPUT_FILE}.exe"

; 新装的默认位置：英文目录名
InstallDir "$LOCALAPPDATA\Programs\${APP_DIR}"
; 上次装在哪儿就默认填哪儿（升级 / 自动更新会直接覆盖原目录，不会把数据挪走）
InstallDirRegKey HKCU "${APP_REG_KEY}" "InstallPath"

RequestExecutionLevel user
BrandingText "${APP_NAME} ${APP_VERSION}"
ShowInstDetails nevershow
ShowUninstDetails nevershow
AllowRootDirInstall false
XPStyle on

; ===========================================================================
;  界面（MUI2 标准向导）
; ===========================================================================
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

; 中文用微软雅黑，默认的宋体在高分屏上发糊
!define MUI_FONT "Microsoft YaHei UI"
!define MUI_FONTSIZE 9

!define MUI_ABORTWARNING

; 安装包图标与卸载图标都直接用程序自己的 ico，不用另外做图
!define MUI_ICON   "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"

; ---------- 欢迎 ----------
!define MUI_WELCOMEPAGE_TITLE "欢迎安装 ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "即将在你的电脑上安装 ${APP_NAME} ${APP_VERSION}。$\r$\n$\r$\n发行者：${APP_PUBLISHER}$\r$\n安装位置：默认放在当前用户目录下，下一步可以改。$\r$\n$\r$\n点「下一步」继续，中途随时可以点「取消」退出。"
!insertmacro MUI_PAGE_WELCOME

; ---------- 安装位置 ----------
!define MUI_DIRECTORYPAGE_TEXT_TOP "选择 ${APP_NAME} 的安装位置。$\r$\n$\r$\n如果之前装过，这里已经自动填好原来的路径——直接点「下一步」就是覆盖升级，原来的收藏和设置都会保留。"
!insertmacro MUI_PAGE_DIRECTORY

; ---------- 快捷方式 ----------
!insertmacro MUI_PAGE_COMPONENTS

; ---------- 安装进度 ----------
!insertmacro MUI_PAGE_INSTFILES

; ---------- 完成 ----------
!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "${APP_NAME} ${APP_VERSION} 已经装好。$\r$\n$\r$\n安装位置：$INSTDIR"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

; ---------- 卸载 ----------
!define MUI_UNTEXT_CONFIRM_TITLE "卸载 ${APP_NAME}"
!define MUI_UNTEXT_CONFIRM_TEXT "将从你的电脑上移除 ${APP_NAME}。$\r$\n$\r$\n注意：安装目录下的所有内容都会被删除，包括收藏、缓存和已下载的文件，此操作不可恢复。"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ===========================================================================
;  初始化
; ===========================================================================
Function .onInit
  SetShellVarContext current

  ; 只允许一个安装实例
  System::Call 'kernel32::CreateMutexW(p 0, i 0, t "${APP_MUTEX}") p .r0 ?e'
  Pop $1
  ${If} $1 == 183
    MessageBox MB_OK|MB_ICONSTOP "${APP_NAME} 安装程序已经在运行。"
    Quit
  ${EndIf}

  ; 静默安装（/S）不需要在这里做任何事：
  ; NSIS 会自动跳过所有页面，按顺序执行下面的 Section。
FunctionEnd

; 目标目录里的程序在跑就先关掉（升级场景下是常态，静默处理，不打断用户）
Function CloseRunningApp
  nsExec::ExecToStack '"$WINDIR\System32\cmd.exe" /c tasklist /fi "imagename eq ${APP_EXE}" /nh | "$WINDIR\System32\findstr.exe" /i /c:"${APP_EXE}"'
  Pop $0   ; 退出码：0=在运行 1=没找到
  Pop $1
  ${If} $0 != 0
    Return
  ${EndIf}

  nsExec::Exec '"$SYSDIR\taskkill.exe" /f /im "${APP_EXE}"'
  Pop $0
  Sleep 800
FunctionEnd

; ===========================================================================
;  安装
; ===========================================================================
Section "主程序" SEC_MAIN
  SectionIn RO

  Call CloseRunningApp

  SetShellVarContext current
  ; 只建目录，绝不 RMDir：老用户的 dbs\ / f\ 数据要留着
  CreateDirectory "$INSTDIR"
  SetOutPath "$INSTDIR"
  SetOverwrite on

  File /r "${SOURCE_DIR}\*.*"

  ; 改名前的旧 exe / 旧快捷方式顺手清掉，避免升级后出现两个入口
  Delete "$INSTDIR\${LEGACY_EXE}"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$QUICKLAUNCH\${APP_NAME}.lnk"

  ; 开始菜单：启动入口 + 卸载入口（这两项始终创建，不进组件页）
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\uninst.exe"

  WriteUninstaller "$INSTDIR\uninst.exe"

  ; 记住安装位置：下次安装（含自动更新）自动填回
  WriteRegStr HKCU "${APP_REG_KEY}" "InstallPath" "$INSTDIR"
  WriteRegStr HKCU "${APP_REG_KEY}" "Version" "${APP_VERSION}"
  WriteRegStr HKCU "${APP_REG_KEY}" "Exe" "$INSTDIR\${APP_EXE}"

  ; 「应用和功能」里的卸载条目
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninst.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninst.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "创建桌面快捷方式" SEC_DESKTOP
  SetShellVarContext current
  Delete "$DESKTOP\${APP_NAME}.lnk"
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "添加到快速启动栏" SEC_QUICK
  SetShellVarContext current
  Delete "$QUICKLAUNCH\${APP_NAME}.lnk"
  CreateDirectory "$QUICKLAUNCH"
  CreateShortCut "$QUICKLAUNCH\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

; 组件页里每项的说明文字
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN}    "程序主体文件，必装。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "在桌面放一个 ${APP_NAME} 的快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_QUICK}   "在任务栏左侧的快速启动栏里放一个入口。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ===========================================================================
;  卸载
; ===========================================================================
Function un.onInit
  SetShellVarContext current

  ; 卸载器的 $INSTDIR 默认就是 uninst.exe 所在目录；万一被挪走，用注册表兜底
  ${If} $INSTDIR == ""
    ReadRegStr $INSTDIR HKCU "${APP_REG_KEY}" "InstallPath"
  ${EndIf}
  ${If} $INSTDIR == ""
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${APP_DIR}"
  ${EndIf}
FunctionEnd

Section "un.主程序"
  ; 程序还开着就关掉，否则文件删不掉
  nsExec::Exec '"$SYSDIR\taskkill.exe" /f /im "${APP_EXE}"'
  Sleep 400

  SetShellVarContext current
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$QUICKLAUNCH\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  ; -------------------------------------------------------------------------
  ; 删掉安装目录下的全部内容（程序文件 + dbs\ 数据库 + f\ 缓存与下载 + 任何其它残留）
  ;
  ; 老版本这里是逐个 Delete 白名单扩展名，漏掉 .db / .jpg / .log 之类就会留一堆垃圾；
  ; 现在整目录递归删，装过什么就清什么。
  ;
  ; 递归删除前先挡一道：$INSTDIR 为空、太短、或者正好等于几个系统目录时一律跳过。
  ; 卸载器可能被挪到别处运行（或注册表里的 InstallPath 被人改坏），这种时候宁可留文件，
  ; 也不能把用户的整个盘或桌面删了。
  ; -------------------------------------------------------------------------
  StrLen $0 "$INSTDIR"
  ${If} $INSTDIR == ""
  ${OrIf} $0 < 8
  ${OrIf} $INSTDIR == "$PROGRAMFILES"
  ${OrIf} $INSTDIR == "$PROGRAMFILES32"
  ${OrIf} $INSTDIR == "$PROGRAMFILES64"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA"
  ${OrIf} $INSTDIR == "$APPDATA"
  ${OrIf} $INSTDIR == "$WINDIR"
  ${OrIf} $INSTDIR == "$SYSDIR"
  ${OrIf} $INSTDIR == "$TEMP"
  ${OrIf} $INSTDIR == "$DESKTOP"
  ${OrIf} $INSTDIR == "$DOCUMENTS"
  ${OrIf} $INSTDIR == "$PROFILE"
    DetailPrint "安装目录参数不正常（$INSTDIR），已跳过文件删除。请手动检查后删除。"
  ${Else}
    ; 删一次不干净是常有的事：刚装完的主程序还压在杀毒软件的实时扫描里，
    ; 系统「已删除文件还原」也偶尔会挡一下。所以最多重试 4 轮，每轮间隔 0.5 秒。
    StrCpy $1 0
    ${Do}
      RMDir /r "$INSTDIR"
      ${IfNot} ${FileExists} "$INSTDIR\*.*"
        ${Break}
      ${EndIf}
      Sleep 500
      IntOp $1 $1 + 1
    ${LoopUntil} $1 >= 4

    ; 还是删不掉（文件一直被别人抱着）就登记到重启后再删，别硬留一个目录给用户
    ${If} ${FileExists} "$INSTDIR\*.*"
      RMDir /r /REBOOTOK "$INSTDIR"
    ${EndIf}
  ${EndIf}

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_REG_KEY}"

  ; RMDir /r 已经把 uninst.exe 一起带走了；这里再兜一次底，防止自删那一瞬被占用
  Delete "$INSTDIR\uninst.exe"
  RMDir "$INSTDIR"
SectionEnd
