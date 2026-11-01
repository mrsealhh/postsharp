# 分发与包管理器

PostSharp 的桌面包按平台发布为 ZIP；Windows 额外有 NativeAOT 通道。仓库内的
`packaging/` 提供 winget、Scoop 和 Homebrew 清单模板，发布新版本后可一键生成。

## 生成清单

先按版本构建 Windows 与 macOS 包：

```powershell
.\scripts\build-release.ps1 -Version 1.11.4
.\scripts\build-osx-release.ps1 -Version 1.11.4 -RuntimeIdentifier osx-arm64
.\scripts\update-packaging.ps1 -Version 1.11.4
```

脚本从 `artifacts/PostSharp-win-x64.zip.sha256` 和
`artifacts/PostSharp-osx-arm64.zip.sha256` 读取校验值，更新：

- `packaging/winget/PostSharp.yaml`
- `packaging/scoop/postsharp.json`
- `packaging/homebrew/postsharp.rb`

## winget

当前清单是**本仓库模板**，还没有提交到 `microsoft/winget-pkgs`。可先用本地清单测试：

```powershell
winget install --manifest .\packaging\winget\PostSharp.yaml --accept-package-agreements
```

提交上游时，把 `PostSharp.yaml` 放到
`manifests/m/Mrsealhh/PostSharp/<版本>/`，校验值使用 ZIP 的 SHA256。

## Scoop

可把清单加入自建 bucket，或直接安装本地清单：

```powershell
scoop install .\packaging\scoop\postsharp.json
```

清单包含 `checkver` 和 `autoupdate`，bucket 维护者执行 `scoop update` 即可跟进新版本。

## Homebrew

macOS arm64 使用 cask 模板。可先在本地安装：

```bash
brew install --cask ./packaging/homebrew/postsharp.rb
```

长期分发建议建立 `mrsealhh/homebrew-postsharp` tap，把 `postsharp.rb` 放到 `Casks/` 后：

```bash
brew tap mrsealhh/postsharp
brew install --cask postsharp
```

## GitHub Actions 发布

仓库提供 `.github/workflows/release.yml`，在 Actions 页面手动运行：

- 输入版本号（不带 `v`）。
- 勾选 `publish` 时，构建完成后创建一个 **draft release**；检查资产和校验值后再手动发布。
- 产物包括 Windows/Linux/macOS JIT 包、Windows NativeAOT 包和各自 `.sha256`；构建后会自动更新 `packaging/` 清单。
- 工作流只构建并上传，不会自动把 draft 转成正式版本。

## 说明

- macOS 包未签名/公证；Homebrew cask 只安装可执行文件，不包含 `.app` 图标。
- winget 清单使用 `zip + portable`，安装后命令别名是 `postsharp`。
- NativeAOT 包只用于 Windows 更新通道，不进入包管理器清单。
- 所有 ZIP 都附带 `.sha256`，包管理器清单中的哈希必须与发布资产一致。
