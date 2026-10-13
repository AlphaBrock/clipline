<p align="center">
  <img src="src/Clipline/Assets/Clipline.png" width="128" alt="Clipline 图标">
</p>

<h1 align="center">Clipline</h1>

<p align="center">
  把截图和录屏挂在屏幕顶边的一根绳子上。<br>
  鼠标碰一下顶边它就滑下来，拿走、拖走、打开，都在手边。
</p>

<p align="center">
  Windows 10 2004 及以上 / Windows 11 · x64 与 ARM64 · WinUI 3 + .NET 10
</p>

---

截图之后，图片通常就躺在某个文件夹里，要用的时候还得去翻。Clipline 把最近的截图和录屏晾在屏幕顶边：
截完图，新照片落到绳上晃两下；要用的时候把鼠标推到屏幕顶边，绳子滑下来，单击复制、拖进聊天窗口，或者直接丢进回收站。

## 下载

到 [Releases](../../releases) 下载最新版本。每个版本都有三种形式，各分 x64 和 ARM64：

| 文件 | 适合 |
|:--|:--|
| `Clipline-<版本>-<架构>-setup.exe` | **安装版**，推荐。默认只为当前用户安装，不需要管理员权限，自带卸载程序 |
| `Clipline-<版本>-<架构>-portable.zip` | **便携版**。解压即用，设置和数据都保存在同目录的 `data` 文件夹里，可以放在 U 盘上 |
| `Clipline-<版本>-<架构>.msix` | **MSIX 包**。由 Windows 管理安装、更新和卸载，卸载后不留痕迹 |

每个文件只有 7 MB 左右。.NET 已经包含在内，不用另外安装；界面运行时 **Windows App Runtime 1.7** 则用系统里共享的那一份，很多电脑装其他应用时已经装过。没有的话：

- **安装版**会在安装时自动从微软下载并安装它；
- **便携版**第一次启动时，Windows 会弹窗提示并带你去下载；
- **MSIX** 由应用安装程序自动补齐。

也可以手动安装：[x64](https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe) · [ARM64](https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-arm64.exe)。

### 安装 MSIX

如果 MSIX 用的是自签名证书（Release 里会同时附上 `Clipline.cer`），第一次安装前需要先信任它。用管理员身份打开 PowerShell：

```powershell
Import-Certificate -FilePath .\Clipline.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

之后双击 `.msix` 即可安装。同一个证书只需要信任一次。

## 怎么用

| 操作 | 效果 |
|:--|:--|
| 鼠标停在屏幕顶边（两端角落除外） | 晾衣绳在该屏幕滑下 |
| 移开 | 收起 |
| 截图（Win+Shift+S / Win+PrtScn）或录屏 | 新照片落到绳上并摆动，绳子自动下来一会儿 |
| 单击 | 复制图片；录屏复制为文件 |
| 双击 | 用默认程序打开 |
| 长按 | 图片用"编辑"方式打开（通常是画图）；录屏打开裁剪窗口 |
| 拖进应用 | 发送副本，照片留在绳上 |
| 拖进文件夹或回收站 | 移过去，照片离开绳子 |
| 点 × | 从绳上取下（文件不动；剪贴板截图的副本移到回收站） |
| 点左上角的图钉 | 置顶：排在最前面，不会因为超出数量被取下，启动时也会挂上（默认最多 3 张） |
| 滚轮 / 触控板横滑 | 翻看放不下的照片 |
| 快捷键（默认 Ctrl+Alt+T）或单击托盘图标 | 显示或隐藏 |
| 托盘右键 | 打开截图文件夹、设置、退出 |

### 会挂上哪些截图

- **截图文件夹**：Windows 默认的 `图片\屏幕截图`，可以在设置里换成别的文件夹。
- **录屏**：Snipping Tool 的保存位置，文件夹名随系统语言变化（中文系统是 `视频\屏幕录制`）。
- **Xbox Game Bar**：Win+Alt+PrtScn 截图和 Win+Alt+R 录像，存在 `视频\Captures`。
- **只在剪贴板里的截图**：Snipping Tool、Snipaste、PixPin、ShareX 等工具，以及 QQ、微信、企业微信自带的截图，复制到剪贴板、没有存成文件的，会存成 PNG 再挂上。
  浏览器和聊天里复制的图片不算；如果截图工具同时也存了文件（Snipping Tool 默认如此），只挂那个文件，不重复。
  这些 PNG 存在 Clipline 自己的数据文件夹里，默认超过 30 天就移到回收站（置顶的除外），天数可以在设置里改，设成 0 就不清理。

### 动起来的方式

所有动画来自同一个实时物理模拟，彼此相互影响：

- 绳子是一根有张力的弦，由一串小质点组成，按波动方程运动。照片落上去，波会往两边传；挂得越多、越重，垂得越深，弹得也越慢。
- 每张照片是挂在夹子上的刚体摆，转动惯量按它自己的尺寸算。夹子在绳上滑动、绳子上下弹、整条绳落下来，都会让它摆起来；它的摆动也会反过来拉绳子。
- 风一阵阵沿着绳子吹过去，远处的照片晚一点才动；风停的时候照片会静下来，模拟也跟着休眠，不占 CPU。
- 鼠标扫过照片，按实际速度和接触点推它；划过绳子会拨动绳子；点击会把照片往里按，绳子跟着一沉。
- 新截图从上方掉下来被绳子接住；点 × 时夹子松开，照片掉下去，绳子弹回去。相邻照片摆得太大会碰在一起，然后弹开。
- 开了"减少动画"就没有风和摇摆，照片直接停到位。

## 设置

托盘图标右键 → 设置，或者在已经运行时再启动一次 Clipline。所有设置改了立即生效：
顶边触发与延迟、新截图时放下、全屏时保持安静、同时显示几张、绳上最多保留几张、最多置顶几张、启动时挂上最近几张、
截图和录屏文件夹、剪贴板截图及其自动清理天数、Game Bar、剪贴板文字（保留条数、自动删除天数、最多置顶几条）、
快捷键（可录制新组合）、主题、减少动画、登录时启动。
设置页底部的「关于」里可以看到当前版本和完整的更新日志。

设置、日志和剪贴板截图保存在：

| 版本 | 位置 |
|:--|:--|
| 安装版 | `%LOCALAPPDATA%\Clipline` |
| 便携版 | `Clipline.exe` 旁边的 `data` 文件夹（删掉 `portable.txt` 就改用上一行的位置） |
| MSIX | `%LOCALAPPDATA%\Packages\Clipline_<发布者 ID>\LocalState`，卸载时一并删除 |

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（`winget install Microsoft.DotNet.SDK.10`），不需要 Visual Studio。
运行还需要上面提到的 Windows App Runtime 1.7。

```bash
dotnet run --project src/Clipline
```

加上 `-- --settings` 会在启动时直接打开设置。

### 本地打包

`build/build.ps1` 和 GitHub Actions 用的是同一个脚本，产物放在 `artifacts/`：

```bash
pwsh build/build.ps1
```

```bash
pwsh build/build.ps1 -Target exe -Arch x64,arm64
```

| 参数 | 说明 |
|:--|:--|
| `-Target` | `all`（默认）、`exe`（安装版 + 便携版）、`installer`、`portable`、`msix`，可以写多个 |
| `-Arch` | `x64`（默认）、`arm64`，可以写多个 |
| `-CertificatePath` / `-CertificatePassword` | 给 MSIX 签名的 `.pfx`；不给就生成未签名的 MSIX（无法直接安装） |

安装版需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)（`winget install JRSoftware.InnoSetup`）。

MSIX 需要签名才能安装。第一次可以生成一张自签名证书，以后一直用它：

```bash
pwsh build/new-certificate.ps1 -Password (Read-Host -AsSecureString)
```

它会写出 `build/certs/Clipline.pfx`（私钥，已被 git 忽略）和 `Clipline.cer`，并打印 `.pfx` 的 base64，用来配置下面的 GitHub Secrets。

## 发布新版本

版本号只写在一个地方：`Directory.Build.props` 的 `VersionPrefix`。更新日志在 [`CHANGELOG.md`](CHANGELOG.md)，
设置页的「更新日志」和 GitHub Release 的说明都直接取自它。

1. 平时把改动记在 `CHANGELOG.md` 的 `## [Unreleased]` 下。
2. 发版时把这些条目移到新的 `## [x.y.z] - YYYY-MM-DD` 标题下，并把 `VersionPrefix` 改成 `x.y.z`。
3. 提交后推送标签：

   ```bash
   git tag v0.2.0 && git push origin v0.2.0
   ```

[Release 工作流](.github/workflows/release.yml) 会检查标签和版本号一致、更新日志里有这一版，然后构建 x64 和 ARM64 的全部安装包，
生成 SHA-256 校验文件，以该版本的更新日志为说明创建 Release。也可以在 Actions 页面手动运行它，默认建成草稿。

版本号带后缀（如在 `Directory.Build.props` 里加 `<VersionSuffix>beta.1</VersionSuffix>`）时会发成预发布版本。

在仓库的 Settings → Secrets and variables → Actions 里配置：

| Secret | 内容 |
|:--|:--|
| `MSIX_CERTIFICATE` | `new-certificate.ps1` 打印的 `.pfx` base64 |
| `MSIX_CERTIFICATE_PASSWORD` | 生成证书时设的密码 |

没有配置时，工作流会用一次性的证书签名并给出警告。那样每个版本的证书都不同，用户每次都要重新信任，所以正式发布前请配置好。

## 项目结构

```
.
├── .github/workflows/
│   ├── ci.yml                  每次推送和 PR：编译 x64 与 ARM64，检查更新日志
│   └── release.yml             推送 v* 标签：打包全部格式并发布 Release
├── build/
│   ├── build.ps1               本地和 CI 共用的打包脚本
│   ├── version.ps1             读取版本号、从更新日志提取发布说明
│   ├── new-certificate.ps1     生成 MSIX 签名证书
│   ├── installer/              Inno Setup 安装程序脚本和简体中文语言文件
│   └── icon/                   图标的 SVG 源文件和 make-icon.ps1
├── docs/                       图标预览等文档图片
├── src/Clipline/
│   ├── App.xaml(.cs)           启动、单实例、日志
│   ├── Package.appxmanifest    MSIX 清单（版本号和发布者在构建时填入）
│   ├── Assets/                 程序图标；Package/ 下是 MSIX 磁贴
│   ├── Core/                   设置、应用信息（版本、数据位置）、更新日志解析
│   ├── Views/                  晾衣绳窗口、照片卡片、设置、录屏裁剪
│   ├── Physics/                绳子、摆动、风和碰撞的物理模拟
│   ├── Captures/               监听截图和录屏文件夹、剪贴板截图、视频缩略图
│   └── Platform/               Win32 调用、托盘和快捷键、开机启动、主题和窗口外观
├── CHANGELOG.md                更新日志（应用内和 Release 共用）
├── Directory.Build.props       版本号
├── global.json                 .NET SDK 版本
└── Clipline.slnx
```

改了 `build/icon/` 里的 SVG 后，运行 `pwsh build/icon/make-icon.ps1` 重新生成 `.ico` 和 MSIX 磁贴（用系统自带的 Edge 渲染）。

## 还没做的

- 界面多语言（目前只有简体中文）
- 应用内检查更新

## 致谢

Clipline 是 [Tendedero](https://github.com/alejandrobujan/tendedero) 的 Windows 版本，玩法和"晾衣绳"的创意都来自它。感谢 Alejandro Buján。

## 许可

[MIT](LICENSE)
