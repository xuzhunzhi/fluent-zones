# 发布与分发（两条渠道）

Fluent Zones 同时走两条渠道，产物都由 `package-all.cmd` 一次生成：

| 渠道 | 产物 | 权限能力 | 定位 |
|---|---|---|---|
| **Microsoft Store** | `.msix` | 普通权限（打包应用**不能**请求管理员） | 付费 ¥12 作赞助费，商店负责签名、更新、卸载 |
| **GitHub Releases** | 便携版 `.zip`、安装包 `Setup-x64.exe`、可选 `.msix` 侧载 | 可「以管理员身份运行」，安装包自带「Fluent Zones（管理员）」快捷方式 | 需要操作 Windhawk 这类提权窗口的用户走这里 |

> 打包（MSIX）应用以普通令牌运行，受 UIPI 限制动不了管理员窗口，商店政策也不允许提权；
> 这正是 EXE / 便携版渠道存在的理由。

## 1. 本地构建

依赖：

- .NET 8 SDK（`build.cmd` 优先用 `%USERPROFILE%\.dotnet\dotnet.exe`）；
- Windows SDK 的 `makeappx.exe` / `signtool.exe`（Windows Kits\10\bin，脚本自动找最高版本）；
- Inno Setup 6：`winget install JRSoftware.InnoSetup`（安装到 `%LOCALAPPDATA%\Programs\Inno Setup 6`）。

命令：

```bat
package-all.cmd
:: 或者
powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1
```

产物（`dist\`）：

| 文件 | 说明 |
|---|---|
| `fluent-zones-v<版本>-win-x64.zip` | 便携版：解压双击 `FluentZones.exe`，自包含 .NET 8 + Windows App SDK |
| `FluentZones-<版本>-Setup-x64.exe` | 安装包：装到 `%LOCALAPPDATA%\Programs\Fluent Zones`，不需要管理员 |
| `FluentZones-<版本>-x64.msix` | MSIX（未签名，商店会重签；GitHub 侧载需自行签名） |

参数：

- `-Version 0.2.0`：覆盖版本号（默认取 `src/FluentZones.UI/FluentZones.UI.csproj` 的 `<Version>`）；
- `-SkipPublish`：复用已经 publish 好的 `dist\FluentZones`；
- `-SkipMsix` / `-SkipInstall`：只出其中一部分；
- `-CertPath .\sign.pfx -CertPassword ***`：给 MSIX 签名。

> 版本号只在一处改：csproj。zip / exe / msix 文件名和 manifest 版本都会跟着走。

## 2. Microsoft Store（¥12 赞助费）

1. **Partner Center** 注册开发者账号（个人开发者需要一次性注册费，按微软当期政策为准）。
2. 新建应用，拿到「产品管理 → 产品标识」里的三个值，替换
   [packaging/msix/AppxManifest.xml](msix/AppxManifest.xml)：
   - `Identity Name` ← Package/Identity/Name
   - `Identity Publisher` ← Package/Identity/Publisher（形如 `CN=xxxx`）
   - `Properties PublisherDisplayName`
3. 上传 `dist\FluentZones-<版本>-x64.msix`（或 .msixupload）到「包」页面。
4. 商店列表：
   - 定价选 **一次性购买**，价格设为 **¥12**，在描述里写明这是「赞助 / 支持开发」；
   - 截图、隐私政策链接、年龄分级按向导填。
5. 提交认证。商店会用自己的证书重签，用户安装时看不到证书问题。

需要注意：

- Store 包无法管理员运行；需要提权的功能在商店版里不可用（遇到提权窗口只会提示）。
- 上架用的身份必须和 Partner Center 完全一致，否则包会被拒。
- 商店版更新走商店，不需要自己再做更新器。

## 3. GitHub Releases

`.github/workflows/release.yml` 会在推送 `v*` tag（或手动触发）时：

1. `windows-latest` 上装 .NET 8 与 Inno Setup；
2. 跑 `packaging/build-release.ps1` 生成 zip + msix + setup；
3. 把三个文件挂到 Release（手动触发时只上传 artifacts）。

本地也可以直接 `package-all.cmd` 后手动上传 `dist\` 里的三个文件。

### 3.1 MSIX 侧载签名（GitHub 渠道）

未签名 MSIX 无法直接安装（会报 `0x80073D2C`，发布者不在未签名命名空间）；
自签证书如果没被信任，会报 `0x800B0109`（根证书不受信任）。做法：

```powershell
# 1) 建自签证书（Subject 必须等于 manifest 里的 Publisher，例如 CN=XuZhunzhi）
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=XuZhunzhi" `
    -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(3)

# 2) 导出 pfx（用来签名）和 cer（给用户装信任）
Export-PfxCertificate -Cert $cert -FilePath sign.pfx -Password (Read-Host -AsSecureString)
Export-Certificate   -Cert $cert -FilePath sign.cer

# 3) 签名（build-release.ps1 也可以带 -CertPath/-CertPassword 自动签）
powershell -File packaging\build-release.ps1 -CertPath sign.pfx -CertPassword ***
```

用户侧安装侧载包，需要先把 `sign.cer` 装进「本地计算机 → 受信任人」（管理员），
或者直接在**开发人员模式**下、且证书已受信任时安装。要在 GitHub 上正式分发，
建议购买代码签名证书（OV/EV），不要用自签。

CI 里签名：配 `MSIX_PFX_BASE64`、`MSIX_PFX_PASSWORD` 两个 secrets（workflow 已留好步骤）。

### 3.2 管理员权限模式

- **程序内开关**：「选项 → 启动 → 管理员模式启动」打开后，每次启动自动用 `runas` 重新拉起自己
  （UAC 被拒绝就按普通权限继续）；MSIX 打包版里这一项置灰不可用。
- **便携版**：右键 `FluentZones.exe` →「以管理员身份运行」；
- **安装版**：开始菜单里的「Fluent Zones（管理员）」快捷方式（用 RunAs 拉起主程序），
  普通启动时遇到提权窗口，程序也会提示「以管理员身份重启」；
- 开机启动写的是 `HKCU\...\Run`（普通权限）。若希望开机后就能操作管理员窗口，
  用「任务计划程序」建一个「使用最高权限运行」的登录任务，替代开机启动开关。

## 4. 许可证提醒（重要）

- **Inno Setup 6.5+ 改为商业授权模式**：非商业用途免费；用它构建、分发**收费**产品属于商业用途，
  需要购买商业许可证（一次性，永久）。编译时输出里的 `Non-commercial use only` 就是在提示这一点。
  可选做法：购买许可证，或改用免费商用的 NSIS / WiX 重做安装包脚本。
- MSIX 商店上架本身没有费用以外的构建工具授权问题；微软商店对 ¥12 定价没有额外限制。

## 5. 发布检查清单

- [ ] 改 `FluentZones.UI.csproj` 的 `<Version>`（例如 `0.2.0`）；
- [ ] 更新 [README.md](../README.md) 的版本/功能说明；
- [ ] `package-all.cmd` 三件产物齐全；
- [ ] 便携版解压能启动、安装包能装能卸、`Ctrl+Alt+0` / 分区模式正常；
- [ ] 需要商店的话，先替换 manifest 身份再打包/上传；
- [ ] 打 tag `v0.2.0` 推 GitHub，确认 Actions 产物与 Release 附件。
