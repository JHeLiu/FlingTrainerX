# .pro · 构建打包与发布

两个入口，双击就行：

- **`publish.bat`** —— 编译 → 打包成安装包 → 记录版本
- **`release.bat`** —— 把最新的安装包发布到 Gitee 发行版

```
.pro/                    ← 脚本目录（只有脚本，不放产物）
├── publish.bat         双击：构建 + 打包
├── publish.ps1         主脚本（版本交互 / dotnet publish / 调 NSIS / 写版本记录）
├── release.bat         双击：发布到 Gitee Releases
├── release.ps1         主脚本（建发行版 / 上传安装包，带上传进度，全程自动）
├── installer.nsi       NSIS 安装程序（MUI2 标准向导，含卸载）
├── version.txt         上一版本号（自动生成）
└── gitee.token.txt     Gitee 私人令牌（本机私有，已被 .gitignore 忽略；没有会当场问一次再写回来）

.releases/               ← 产物目录（项目根目录下，已被 .gitignore 忽略）
└── 1.2.4/              每次构建新增一个版本目录，历史版本保留
    ├── publish/        dotnet publish 产物
    └── 风灵月影宗藏宝阁-1.2.4.exe

FlingTrainerX/           ← 项目目录
└── config.json         ← 程序配置（版本号 + 启动是否自动检查更新）；publish.ps1 在编译前改写，编译时复制到 exe 旁边
```

### config.json

程序不再用程序集版本，而是**启动时**读 exe 旁边的 `config.json`，把配置存进静态字段：

```json
{
  "curVersion": "1.2.4",
  "autoUpdateCheck": true
}
```

- `curVersion` —— 当前版本号，设置页的「当前版本 v…」和在线更新的新旧判断都取它
- `autoUpdateCheck` —— 启动后要不要自动检查更新。用户在更新弹窗里点过「**取消更新**」，
  程序会把它改成 `false` 并按这个值跳过以后的启动检查（设置页手动点「检查更新」不受影响）；
  字段缺失 / 文件缺失时按 `true` 处理
- 文件缺失 / 写坏 / 版本号不合法时，程序自动退回程序集版本（csproj 的 `<Version>`），不会崩
- 启动时读一次就缓存，改完文件要重启程序才生效（`autoUpdateCheck` 由程序自己改的那次除外，当场生效）

**不用手改**：`publish.ps1` 在 `dotnet publish` 之前会把输入的版本号写进 `FlingTrainerX\config.json`，
并把 `autoUpdateCheck` 重置成 `true`（升级到新版后恢复自动检查，用户再取消才会又变 false），
csproj 再把它复制到输出目录根部，所以装完的 `Programs\FlingTrainerX\config.json` 就是这一版。

---

## 一、构建

1. 双击 `publish.bat`
2. 显示**上一版本号**（读 `version.txt`，没有则显示「无」）
3. 输入新版本号，必须是 `x.x.x`，格式不对会重新问
4. 回车确认 → 自动跑 3 步，带进度条和耗时
   编译前脚本会先把版本号写进 `config.json`（程序读它显示当前版本、决定要不要自动检查更新）
5. 结束打印安装包路径，按回车关闭

```
  ================================================
   风灵月影宗藏宝阁 · 构建打包
  ================================================

  上一版本：1.2.3
  请输入新版本号（格式 x.x.x，例如 1.2.3）
  > 1.2.4
  将构建版本 1.2.4，确认继续吗？(Y/N)
  > y

  [1/3] 编译（dotnet publish）
        · E:\...\.releases\1.2.4\publish
        ############################ 100%  编译中        00:52
        √ 编译完成 · 272.3 MB
  [2/3] 打包（NSIS）
        ...
        √ 打包完成 · 72.7 MB（原始 272.3 MB，压到 27%）
  [3/3] 记录版本号
        √ version.txt → 1.2.4
```

任何一步失败都会打印原因 + 日志路径，并停住窗口等你按键。
历史版本目录只新增不删除，只会清掉本次版本自己的 `publish/`。
产物全部落在项目根目录的 `.releases\<版本号>\`，脚本目录保持干净。

### 可配置项（`publish.ps1` 顶部）

| 变量 | 说明 |
| --- | --- |
| `$SoftwareName` | 软件名。安装包命名、快捷方式名、注册表键都用它 |
| `$Publisher` | 发行者，写入「应用和功能」 |
| `$ProjectFile` | csproj 路径，相对 `.pro` 目录 |
| `$ReleaseDir` | 产物根目录，相对 `.pro`，默认 `..\.releases` |
| `$TargetFramework` / `$RuntimeId` | `net10.0-windows` / `win-x64` |
| `$AppIcon` | 安装包与卸载项的图标 |
| `$NsisExe` | `makensis.exe` 路径。默认留空，按命令名从 PATH 查找（`makensis` / `makensis.exe` 都认）；只有想指定某个 NSIS 安装时才填完整路径 |
| `$ReadyToRun` | 是否 ReadyToRun，默认 `$true`；改 `$false` 安装包小 19MB |
| `$CompressDictMB` | NSIS 的 LZMA 字典大小，默认 `64` |

构建命令固定为：

```
dotnet publish -c Release -f net10.0-windows -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=<True|False> -p:Version=<版本号> -o <publish 目录>
```

> 程序目录 `Assets\` 下有两个纯文本文件会一起打进安装包，改文案不用重新编译：
> `disclaimer.txt`（「关于」弹窗正文）、`changelog.txt`（设置页「更新内容」）。
> 改完重新 `publish` 一次即可；程序每次打开对应界面都会重读，UTF-8 保存就行。
>
> 文件名一律用英文：中文文件名在 NSIS 打包、某些解压工具、以及被别的进程
> 按路径引用时都容易出编码问题。正文内容当然还是中文。

---

## 二、安装程序

`installer.nsi` 用 NSIS 自带的 **MUI2 标准向导**，不自己画界面：

1. 欢迎页
2. 选择安装位置（默认填上次装的路径）
3. 选择组件：桌面快捷方式 / 快速启动栏
4. 安装进度
5. 完成（可勾选「立即运行」）

没有许可协议页，也没有任何免责声明页。

要点：

- **默认装在英文目录**：`$LOCALAPPDATA\Programs\FlingTrainerX`（`installer.nsi` 里的
  `APP_DIR`）。窗口标题、快捷方式、注册表显示名仍然用中文的 `APP_NAME`，
  只有文件夹名是英文——中文路径在命令行、杀软白名单、手动敲路径时都容易出问题。
- **记住安装位置**：`InstallDirRegKey` 读 `HKCU\Software\<软件名>\InstallPath`，下次自动填回。
  注意这条的优先级高于默认值，所以**已经装在中文目录里的老用户，升级时仍会留在原目录**
  ——程序的数据（`dbs\`、`f\`）就在安装目录里，硬迁有丢数据风险，宁可不动。
  只有全新安装才会落到 `Programs\FlingTrainerX`。
- **卸载删干净**：整个安装目录递归删掉，`dbs\`（数据库）、`f\`（缓存与下载的文件）
  以及任何其它残留一起清空，目录本身也删除。删之前会先挡一道——`$INSTDIR` 为空、
  太短、或正好是 Program Files / 桌面 / 用户目录这类系统路径时跳过删除，
  免得卸载器挪到别处运行时把整个盘删掉；一次删不干净会自动重试 4 轮，
  仍被占用就登记到重启后再删
- **静默安装**：`安装包.exe /S`，跳过所有页面直接装完
- **升级前自动关闭**正在运行的旧版本，不弹框打断
- 界面字体在 `MUI_FONT` 改成微软雅黑；想换欢迎页插图设 `MUI_WELCOMEFINISHPAGE_BITMAP`

---

## 三、体积与压缩

`installer.nsi` 顶部用的是 **LZMA + solid** 压缩：

```nsis
SetCompressor /SOLID lzma
SetCompressorDictSize ${COMPRESS_DICT_MB}   ; 由 publish.ps1 传入，默认 64
SetDatablockOptimize on
```

NSIS 默认是 zlib 且非 solid，对 .NET 单文件几乎压不动。实测：

| 方案 | publish 目录 | 安装包 | 打包耗时 |
| --- | --- | --- | --- |
| zlib 非 solid（NSIS 默认） | 273 MB | 111 MB | ~40 秒 |
| **LZMA solid · 字典 64MB（当前）** | 273 MB | **72.7 MB** | ~2 分 40 秒 |
| LZMA solid · 字典 128MB | 273 MB | 72.6 MB | ~2 分 45 秒 |
| LZMA solid · 字典 64MB + 关 R2R | 193 MB | **54.2 MB** | ~2 分 |

- **字典 128MB 没必要**：只小 0.2MB，解压时却要多占 64MB 内存
- **想再小就把 `$ReadyToRun` 改成 `$false`**：能省 19MB，代价是首次启动慢 100~300ms
- **不要开 `EnableCompressionInSingleFile`**：exe 内部先压过一遍，LZMA 就压不动了，反而更大
- **WPF 不能 `PublishTrimmed`**：官方不支持，开了运行时崩
- LZMA 解压不慢：72.7MB 的包装完 272MB 只用 **12 秒**

---

## 四、发布到 Gitee

双击 **`release.bat`**，**基本全程自动**（第一次发布要粘一次令牌，之后不再问）：

0. 取令牌：读 `gitee.token.txt`；没有就当场要你粘一次，粘完自动写进这个文件
1. 读 `version.txt` 拿版本号，找 `.releases\<版本号>\<软件名>-<版本号>.exe`
2. 读 `FlingTrainerX\Assets\changelog.txt` 当发行版描述
3. 查 Gitee 上有没有同 tag 的发行版
   - 没有 → 新建
   - 有 → 把描述刷成 `changelog.txt` 的最新内容（PATCH 失败就删掉重建）
4. 删掉发行版里的旧附件 → 上传新的安装包（带真实上传进度条）

同一个版本号重复跑是安全的，结果只会是「描述更新 + 附件换新」，不会堆出一串附件。

### 附件名与源码包

上传的附件名就是安装包的文件名，中文原样保留（例如 `风灵月影宗藏宝阁-1.0.2.exe`）。

> 这里不能用 .NET 的 `MultipartFormDataContent`：它会把非 ASCII 文件名转成
> RFC2047 编码字（`filename="=?utf-8?B?6aOO...?="`），Gitee 不会还原，
> 直接把这串东西当真实文件名存下来。后果是后缀 `.exe` 被吞进 base64，
> 客户端认不出安装包，会退而去下载源码 zip——用户点了更新什么也装不上。
> 所以 `release.ps1` 里的上传器是手写 multipart 头尾，文件名按 UTF-8 原样写。

另外，Gitee 会给**每个发行版自动挂** `源码.zip` / `源码.tar.gz`，这是平台行为，
删不掉、也关不掉，但：

- 它们不占 1GB 附件额度（`attach_files` 接口里看不到它们）
- 客户端 `UpdateService.PickAsset` 会跳过 `/archive/refs/` 开头的地址，不会误下

所以发行版页面上看起来有三个文件，真正能装的就是那一个 exe。

用到的接口（Gitee OpenAPI v5）：

| 用途 | 方法 | 路径 |
| --- | --- | --- |
| 按 tag 查发行版 | `GET` | `/repos/{owner}/{repo}/releases/tags/{tag}` |
| 创建发行版 | `POST` | `/repos/{owner}/{repo}/releases`（JSON：`tag_name` / `name` / `body` / `target_commitish`） |
| 更新发行版 | `PATCH` | `/repos/{owner}/{repo}/releases/{id}`（form：`tag_name` / `name` / `body`） |
| 列附件 | `GET` | `/repos/{owner}/{repo}/releases/{id}/attach_files` |
| 删附件 | `DELETE` | `/repos/{owner}/{repo}/releases/{id}/attach_files/{file_id}` |
| 传附件 | `POST` | `/repos/{owner}/{repo}/releases/{id}/attach_files`（`multipart/form-data`，字段名 `file`） |

完成后程序里的「检查更新」立刻就能看到这个版本，走的是 Gitee 的
`releases/latest` 接口（见 `Services\UpdateService.cs`）。

### 令牌

需要一个 Gitee 私人令牌，**勾选 `projects` 权限**：

<https://gitee.com/profile/personal_access_tokens>

`release.ps1` 不从脚本里读令牌，而是读同级目录的 **`gitee.token.txt`**：

- 文件在 → 直接用，控制台只打印头尾（`5071…00c0a4`）方便确认用的是哪把
- 文件不在 / 内容是空的 → 当场要你粘贴一次（输入不回显），**粘完自动写进这个文件**，以后发布都不再问
- 换令牌 = 直接改这个文件；想重新问一遍 = 删掉它

> 文件里只认第一个非空行，前后空格、引号、BOM 都会自动去掉，用记事本随手编辑也没事。

> ✅ `gitee.token.txt` 已进 `.gitignore`，**不会**跟着 git 提交。
> 令牌只在你本机，不会再出现「令牌被 push 到公开仓库」这种事。
> 早先版本里写死在 `release.ps1` 的那把令牌仍在 git 历史里，
> 建议到令牌页面把它删掉、重新生成一把填进 `gitee.token.txt`。

> 仓库改成**公开**也不能省掉这个令牌。公开只免掉「读」——
> 程序端检查更新、用户下载安装包都是匿名 GET，不需要令牌；
> 而发布要建发行版、传附件、改描述，都是写操作，
> Gitee 对任何仓库（公开或私有）都要求认证。实测公开库匿名
> `POST /releases` 直接返回 `401`。
>
> 反过来说，公开库是更省事的选择：不用把令牌塞进客户端，
> 也就不存在令牌随安装包外泄的风险。客户端只做匿名 GET，
> 仓库地址写死在 `Services\UpdateService.cs` 的 `DefaultOwner` / `DefaultRepo` 里。

### 客户端不需要配置文件

仓库地址在 `UpdateService.cs` 里写死（`misslius/FlingTrainerX`），
所以安装目录里**没有** `update.json`，改仓库要改代码重新编译。

真遇到要临时换仓库的情况，在 exe 旁边手放一个 `Assets\update.json`
也能生效（`LoadConfig` 的读取逻辑还留着），但注意它会被任何拿到安装包的人读到，
**不要往里面填带写权限的令牌**。

### 发行说明（描述）

正文就是 `FlingTrainerX\Assets\changelog.txt` 的原文，一行不改地发上去。
发版前改这个文件就行，不用重新编译、也不用改脚本：

```
v1.0.3

- 修掉 xxx
- 新增 yyy
```

文件不存在或为空时，脚本会自动生成一段（版本号 / 构建时间 / 安装包名）顶着，不会发空描述。

### tag 规则

`v` + 版本号，例如 `v1.2.4`。程序端解析时会自动去掉 `v` 前缀。

---

## 五、构建时自动生成的文件

`publish.ps1` 每次都会重写，**不要手动改**：

- `installer-config.nsh` —— 软件名、版本、源目录、输出目录、图标路径

`release.ps1` 不会生成文件，只在 Gitee 上建发行版。

---

## 六、常见问题

**`Bad text encoding`**
`installer.nsi` / `installer-config.nsh` 必须是**带 BOM 的 UTF-8**。

**`找不到 makensis.exe`**
先把 NSIS 安装目录（含 `makensis.exe`）加进系统 PATH，然后**重开终端**再跑脚本。
不想改 PATH 时，才在 `publish.ps1` 顶部把 `$NsisExe` 填成完整路径。

**`发布目录里没有 XXX.exe`**
csproj 的 `AssemblyName` 与 `$SoftwareName` 不一致，两边改成同一个名字。

**发布时报 401 / 403**
令牌无效，或者没勾 `projects` 权限。

**发布时报 413**
文件超过了 Gitee 的附件大小上限，需要分卷或改用网盘。

**打包很慢（2~3 分钟）**
LZMA solid 压缩本来就慢，这是用时间换体积。想快可以把 `SetCompressor` 改回 `zlib`，
但安装包会从 72MB 涨回 111MB。
