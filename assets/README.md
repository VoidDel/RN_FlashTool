# 图标

- `icon.svg`：图标源文件
- `icon.png`：由 `icon.svg` 生成的 512×512 位图

程序实际使用的是根目录 `icons/` 下的各尺寸图标（含 Windows 的 `icon.ico` 与 macOS 的 `icon.icns`），
改了 `icon.svg` 之后用 Tauri CLI 重新生成：

```bash
npx @tauri-apps/cli@2 icon assets/icon.svg -o icons
```

生成后可删掉 `icons/` 里的 `android/`、`ios/` 与 `Square*Logo.png` / `StoreLogo.png`，桌面版用不到。
