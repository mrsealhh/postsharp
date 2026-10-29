# macOS 运行说明

当前 macOS 包是未签名、未公证的 `.zip` 预览包，用于功能验证；正式 `.dmg` / 签名 / 公证仍属于后续工作。

## 启动

```bash
unzip PostSharp-osx-arm64.zip -d postsharp
cd postsharp
bash run.command
```

如果 Finder 阻止运行，可在终端执行：

```bash
chmod +x PostSharp run.command
xattr -dr com.apple.quarantine .
./PostSharp
```

需要安装 .NET 10 时，自包含包不需要额外运行时；如果使用框架依赖包，请先安装对应 .NET SDK/运行时。

## 签名与公证

当前包未签名、未公证，`xattr` 只是去掉下载隔离属性，不等于通过 Gatekeeper。要彻底消除 macOS 的「无法验证开发者 / 已损坏」提示，需要：

1. Apple Developer Program 账号和 Developer ID Application 证书；
2. 在 macOS 构建机上对 `PostSharp` 及随包 dylib 执行 `codesign --options runtime`；
3. 使用 `xcrun notarytool submit --wait` 提交公证，成功后 `xcrun stapler staple`；
4. 发布公证后的 `.dmg` / `.zip`，并保留 SHA256 校验。

这些步骤依赖 Apple 账号、证书私钥和 macOS 公证环境，当前 Windows 构建流程无法代替。未完成前请把本包当作未签名预览包使用。

## OIDC / OAuth 2.0

默认使用系统默认浏览器登录，回调地址默认：

```text
http://127.0.0.1:53682/callback
```

应用在 `127.0.0.1` 启动临时回环监听，浏览器授权后把 `code` 和 `state` 回传，应用校验 `state` 后用授权码 + PKCE 换取 token。

请在 IdP 客户端登记同一回调地址。若端口被占用，请修改重定向地址和 IdP 登记地址后重新登录。

## 当前限制

- 机密使用本机 AES-GCM 密钥文件保存；macOS Keychain 适配仍待完成。
- 自动更新仅 Windows 启用。
- 当前包未签名、未公证，Gatekeeper 可能要求手动允许运行。
- App 图标、Dock 名称、原生菜单栏和安装器仍待补齐。
