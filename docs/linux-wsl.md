# Linux / WSL 运行说明

本包用于 Linux x64 / WSLg 演示，默认使用系统默认浏览器完成 OIDC/OAuth 2.0 授权，不依赖应用内 WebView2。

## 启动

```bash
unzip PostSharp-linux-x64.zip -d postsharp
cd postsharp
bash run.sh
```

首次启动会在当前目录或 `POSTSHARP_DATA_DIR` 下创建 `.postsharp` 数据目录。也可以用环境变量指定：

```bash
POSTSHARP_DATA_DIR="$HOME/.local/share/postsharp" bash run.sh
```

## WSL 依赖

需要 WSL2 + WSLg（Windows 11，或安装了 WSLg 的 Windows 10），并确保有 X11/Wayland 运行库：

```bash
sudo apt update
sudo apt install -y libx11-6 libice6 libsm6 libfontconfig1 libgl1 \
  libxext6 libxrandr2 libxi6 libxcursor1 libxrender1 libxtst6
```

系统浏览器回调使用 `xdg-open`、`wslview` 或 `sensible-browser`；建议安装 `wslu`：

```bash
sudo apt install -y wslu
```

如果没有可用的浏览器启动器，应用会尝试通过 `cmd.exe` 交给 Windows 浏览器。授权回调地址默认是：

```text
http://127.0.0.1:53682/callback
```

请把同一地址登记到 IdP 的客户端回调白名单。WSL2 通常会把 Windows 的 `localhost` 转发到 WSL；如果浏览器打开后无法回到应用，请在 Windows 浏览器中手动访问授权链接，并检查 Windows 到 WSL 的端口转发。

## 登录流程

1. 在「授权」页选择 OAuth 2.0，填写发现地址、客户端 ID、回调地址和授权范围。
2. 点击「登录」，应用在 `127.0.0.1:回调端口` 启动临时监听，并打开系统默认浏览器。
3. 浏览器完成登录后重定向到本机回调；应用校验 `state`，用授权码 + PKCE 换取 token。
4. token 不会经过浏览器页面；回调页只显示「登录完成，可以关闭」。

## 当前限制

- 应用内 WebView 登录仅 Windows 支持；Linux 使用系统默认浏览器。
- Linux 使用本机 AES-GCM 密钥文件保存机密；Windows 继续使用 DPAPI。
- Linux 包不启用自动更新；请手动下载新包。
- 正式签名、AppImage/deb 打包和完整 Linux 回归仍在后续跨平台工作中。
