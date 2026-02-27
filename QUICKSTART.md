# 快速使用指南

## ✅ 当前状态

**打包已完成！** 程序位于：`dist\STM32_Flash_Tool\`

## 🚀 直接使用

### 方法 1：运行程序
```powershell
cd dist\STM32_Flash_Tool
.\STM32_Flash_Tool.exe
```

### 方法 2：打开文件夹
```powershell
explorer dist\STM32_Flash_Tool
```
然后双击 `STM32_Flash_Tool.exe`

## 📦 创建分发包

### 使用 PowerShell 压缩
```powershell
Compress-Archive -Path dist\STM32_Flash_Tool -DestinationPath STM32_Flash_Tool_v1.0.zip -Force
```

### 使用 7-Zip（推荐，体积更小）
```powershell
7z a -t7z -mx=9 STM32_Flash_Tool_v1.0.7z dist\STM32_Flash_Tool
```

## 🔄 重新打包

如果需要重新打包（修改代码后）：

### 方法 1：使用新的 PowerShell 脚本（无中文，无编码问题）
```powershell
.\build.ps1
```

### 方法 2：使用 CMD
打开 CMD（不是 PowerShell），然后运行：
```cmd
build_package.bat
```

### 方法 3：手动命令
```powershell
# 激活虚拟环境
.\.venv\Scripts\Activate.ps1

# 清理旧文件
Remove-Item -Recurse -Force build, dist -ErrorAction SilentlyContinue

# 打包
pyinstaller stm32_flash.spec --clean --noconfirm
```

## ⚠️ 常见问题

### 问题：PowerShell 脚本中文乱码
**解决**：使用新创建的 `build.ps1`（纯英文，无编码问题）

### 问题：无法运行 .bat 文件
**原因**：在 PowerShell 中不能直接运行 .bat
**解决**：
- 使用 `.\build.ps1` 代替
- 或打开 CMD 运行 `build_package.bat`

### 问题：执行策略错误
```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\build.ps1
```

## 📊 文件说明

| 文件 | 用途 |
|------|------|
| `build.ps1` | PowerShell 打包脚本（推荐，无编码问题） |
| `build_package.bat` | CMD 打包脚本 |
| `build_package.ps1` | 旧的 PowerShell 脚本（有编码问题） |
| `stm32_flash.spec` | PyInstaller 配置文件 |
| `requirements.txt` | Python 依赖列表 |

## 💡 推荐工作流

1. **开发阶段**：直接运行 `python stm32_flash.py`
2. **测试打包**：运行 `.\build.ps1`
3. **创建分发包**：压缩 `dist\STM32_Flash_Tool` 文件夹
4. **分发给用户**：提供压缩包和使用说明

## 📝 当前打包信息

- ✅ 输出目录：`dist\STM32_Flash_Tool\`
- ✅ 可执行文件：`STM32_Flash_Tool.exe`
- ✅ 总大小：约 127MB（未压缩）
- ✅ 包含 OpenOCD：是
- ✅ 需要 Python 环境：否
