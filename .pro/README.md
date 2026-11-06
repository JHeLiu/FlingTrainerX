# 构建与打包脚本

双击 `BuildPackage.bat` 即可启动，脚本都在项目根目录的 `.pro` 文件夹里。

## 流程

1. 显示上一版本（读 `version.txt`，没有记录则显示「无」）
2. 输入新版本号，强制 `x.x.x` 格式，不合法会要求重新输入
3. 二次确认后开始 `dotnet publish`（Release / net10.0-windows / win-x64 / 独立部署 / 单文件 / ReadyToRun），
   版本号用 `-p:Version=<版本号>` 写进 exe，在线更新靠它判断新旧
4. 用 GDI+ 生成整套安装界面位图，调用 Inno Setup（`publish.iss`）打包安装包
5. 把 `publish\` 整个目录打成在线更新用的 zip 更新包
6. 成功后把新版本号写回 `version.txt`

控制台输出 `[1/4] 编译`、`[2/4] 打包`、`[3/4] 更新包`、`[4/4] 版本记录` 和单行进度条；
dotnet / Inno Setup 的详细日志写入临时文件，**只有失败时**才回显末尾 25 行并保留完整日志路径。

## 产物位置

```
.pro\<版本号>\
├── publish\                                  ← dotnet publish 的全部产物
│   ├── 风灵月影宗藏宝阁.exe
│   └── ...（运行所需的原生 dll / Assets 等）
├── 风灵月影宗藏宝阁-安装包-<版本号>.exe      ← Inno Setup 安装包（首次安装用）
└── 风灵月影宗藏宝阁-更新包-<版本号>.zip      ← 在线更新包（传 Gitee 发行版附件）
```

- `.NET` 发布产物在 `.pro\<版本号>\publish\`，安装包和更新包在 `.pro\<版本号>\` 根目录
- 只新增 / 重建本次版本目录，不删除任何历史版本目录
- 安装包只打包 `publish\` 里的内容，安装后不会多出一层 `publish` 目录
- **发版时必须把 zip 更新包传成 Gitee 发行版附件**，否则客户端只能提示、没法自动装

## 安装界面（QQ 音乐风格，100% 自绘）

不使用 MUI2，不加载任何 NSIS 默认向导页，没有许可页、没有免责声明页。
窗口 560×460、无边框、右上角自绘关闭按钮。

| 页面 | 内容 |
| --- | --- |
| 快速安装 | 淡青→淡紫渐变头图 + 同心波纹 + 半透明气泡 + 圆形 Logo + 软件名 + 宣传语 + 版本号；白色区放青绿胶囊「快速安装」、左下「创建桌面快捷方式」勾选、右下「自定义安装 >」 |
| 自定义安装 | 同一窗口内切换（不翻页）：出现安装路径输入框与「浏览」，主按钮变成「开始安装」，右下变成「< 返回」 |
| 安装进度 | 状态文字 + 品牌青绿进度条，主按钮为禁用态「安装中…」 |
| 安装完成 | 绿色对勾 + 「安装完成，即刻启程」，主按钮变成「立即体验」，左下「立即运行」勾选 |

安装动作由进度页的定时器驱动（不依赖 NSIS 默认 instfiles 页），因此 makensis 会输出一条
`warning 8000: Page instfiles not used` —— 这是预期现象，不影响任何功能，实测文件能正常释放。

采用**每用户安装**（`$LOCALAPPDATA\Programs\<软件名>`，写 HKCU），不弹 UAC：
程序运行时要在安装目录写自己的数据目录，放 Program Files 普通用户没有写入权限。
卸载时只删程序文件，保留 `dbs\` / `f\` 等用户数据。

## 环境要求

- .NET 10 SDK，`dotnet` 已加入 PATH
- NSIS 3.x，`makensis.exe` 在 PATH 中；否则修改 `BuildPackage.ps1` 顶部的 `$NsisExe`
- 界面位图由 PowerShell 的 `System.Drawing` 生成，无需安装任何第三方 NSIS 插件

## 文件说明

| 文件 | 用途 |
| --- | --- |
| `BuildPackage.bat` | 双击入口，负责调用 PowerShell 并在结束时暂停窗口 |
| `BuildPackage.ps1` | 主脚本：版本读取 / 输入校验 / 编译 / 生成位图 / 打包 / 记录 |
| `FlingTrainerX.nsi` | NSIS 安装脚本（nsDialogs 全自绘界面，含 12 张位图占位说明） |
| `version.txt` | 版本记录，构建成功后自动更新 |
| `installer-config.nsh` | 构建时自动生成的 NSIS 配置（版本号、路径、位图目录），每次构建覆盖 |
| `installer-assets\*.bmp` | 安装界面位图，共 12 张，每次构建自动生成 |

## 可配置项（`BuildPackage.ps1` 顶部）

- `$AppName` / `$Publisher`：软件名、发布方（影响安装目录、快捷方式、安装包文件名）
- `$ProjectFile` / `$MainExeName`：工程文件、发布后的主程序名
- `$IconFile` / `$LogoFile`：安装包图标、界面头图里的圆形 Logo（相对项目目录）
- `$InstallerSlogan`：头图上的宣传语，默认 `PC 新版 · 即刻启程`
- `$NsisExe`：NSIS 路径（留空自动探测 PATH 与常见安装目录）
- `$PublishSubDir`：发布产物子目录名，默认 `publish`
- `$KeepCustomAssets`：默认 `$false`；改成 `$true` 可保留自己替换的位图，不再自动生成（此时位图里的版本号不会随构建更新）
- `$Configuration` / `$TargetFramework` / `$RuntimeId` / `$SelfContained` / `$PublishSingleFile` / `$PublishReadyToRun`：构建参数
- `$PublishEstimateSeconds` / `$NsisEstimateSeconds`：仅影响进度条估算，不影响构建

## 换掉安装界面图片

所有位图都在 `installer-assets\`，必须是 **24 位 BMP**，尺寸不能改（NSIS 的
`BM_SETIMAGE` 不会缩放图片）。窗口固定 560×460、头图高 290。

| 文件 | 尺寸 | 说明 |
| --- | --- | --- |
| `page-home.bmp` | 560×460 | 快速安装页整窗背景 |
| `page-custom.bmp` | 560×460 | 自定义安装页整窗背景 |
| `page-progress.bmp` | 560×460 | 安装进度页整窗背景 |
| `page-finish.bmp` | 560×460 | 完成页整窗背景 |
| `btn-quick.bmp` | 180×48 | 「快速安装」胶囊 |
| `btn-install.bmp` | 180×48 | 「开始安装」胶囊 |
| `btn-wait.bmp` | 180×48 | 「安装中…」禁用态 |
| `btn-experience.bmp` | 180×48 | 「立即体验」胶囊 |
| `btn-browse.bmp` | 60×30 | 「浏览」小按钮 |
| `btn-custom.bmp` | 126×24 | 「自定义安装 >」文字链接 |
| `btn-back.bmp` | 126×24 | 「< 返回」文字链接 |
| `btn-close.bmp` | 28×24 | 右上角关闭 X（从首页背景右上角裁出，保证渐变无缝） |

按钮位图的**四角必须填成纯白**（关闭按钮除外，它要与头图渐变一致），
否则胶囊按钮会出现方块角。想换成自己的设计：用同名同尺寸文件覆盖，
并把 `$KeepCustomAssets` 设为 `$true`，脚本就不再重新生成。
