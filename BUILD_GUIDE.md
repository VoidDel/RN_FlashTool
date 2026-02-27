# 打包指南

本文档说明如何打包 STM32 固件烧录工具。

## 📋 前置要求

- Python 3.8+
- pip
- 虚拟环境（推荐）

## 🚀 快速打包

### Windows

```bash
# 方式 1: 使用自动化脚本（推荐）
build_package.bat

# 方式 2: 手动打包
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt
pyinstaller stm32_flash.spec --clean --noconfirm
```

### Linux/macOS

```bash
# 方式 1: 使用自动化脚本（推荐）
chmod +x build_package.sh
./build_package.sh

# 方式 2: 手动打包
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
pyinstaller stm32_flash.spec --clean --noconfirm
```

## 📦 打包配置说明

### stm32_flash.spec 配置文件

关键配置项：

```python
# 包含的数据文件
datas=[
    ('openocd', 'openocd'),  # 包含 OpenOCD 工具
],

# 排除不需要的模块（减小体积）
excludes=[
    'matplotlib',
    'numpy',
    'pandas',
    'scipy',
    'PIL',
    'tkinter',
],

# 不打包成单文件
exclude_binaries=True,

# 不显示控制台窗口
console=False,
```

## 📊 打包结果

### 目录结构

```
dist/
└── STM32_Flash_Tool/
    ├── STM32_Flash_Tool.exe    # 主程序 (1.7MB)
    └── _internal/              # 依赖文件 (125MB)
        ├── PySide6/            # Qt 库
        ├── openocd/            # OpenOCD 工具
        ├── python313.dll       # Python 运行时
        └── ...                 # 其他依赖
```

### 体积分析

| 组件 | 大小 | 说明 |
|------|------|------|
| 主程序 | 1.7MB | 启动器 |
| PySide6 | ~80MB | Qt GUI 框架 |
| Python 运行时 | ~20MB | Python 3.13 |
| OpenOCD | ~11MB | 烧录工具 |
| 其他依赖 | ~15MB | 系统库等 |
| **总计** | **~127MB** | 未压缩 |

压缩后：
- ZIP: ~50-60MB
- 7z: ~40-50MB

## 🔧 优化建议

### 减小体积

1. **使用 UPX 压缩**（已启用）
   ```python
   upx=True,
   ```

2. **排除不需要的 PySide6 模块**
   ```python
   excludes=[
       'PySide6.QtWebEngine',
       'PySide6.Qt3D',
       'PySide6.QtCharts',
   ],
   ```

3. **使用 Python 3.11**（体积更小）
   - Python 3.13: ~20MB
   - Python 3.11: ~15MB

### 提升性能

1. **启用字节码优化**
   ```bash
   pyinstaller --optimize=2 stm32_flash.spec
   ```

2. **使用 Nuitka**（编译为 C++）
   ```bash
   pip install nuitka
   nuitka --standalone --windows-disable-console stm32_flash.py
   ```

## 📤 分发流程

### 1. 打包程序

```bash
build_package.bat
```

### 2. 测试

```bash
cd dist/STM32_Flash_Tool
./STM32_Flash_Tool.exe
```

测试清单：
- [ ] 程序能正常启动
- [ ] 界面显示正常
- [ ] 能选择固件文件
- [ ] OpenOCD 路径检测正常
- [ ] 能连接设备（如有硬件）

### 3. 创建压缩包

```bash
# 使用自动化脚本
create_release.bat

# 或手动压缩
cd dist
zip -r STM32_Flash_Tool_v1.0_Win64.zip STM32_Flash_Tool/
```

### 4. 发布

上传到：
- GitHub Releases
- 网盘
- 内部服务器

## 🐛 常见问题

### 问题 1: 打包后程序无法启动

**原因**: 缺少依赖或路径问题

**解决**:
```bash
# 查看详细错误
cd dist/STM32_Flash_Tool
./STM32_Flash_Tool.exe --debug
```

### 问题 2: OpenOCD 未被打包

**原因**: datas 配置错误

**解决**: 检查 spec 文件中的 datas 配置
```python
datas=[
    ('openocd', 'openocd'),  # 确保路径正确
],
```

### 问题 3: 体积过大

**原因**: 包含了不必要的依赖

**解决**: 在 excludes 中添加不需要的模块
```python
excludes=[
    'matplotlib',
    'numpy',
    'pandas',
    # ... 添加更多
],
```

### 问题 4: 杀毒软件误报

**原因**: PyInstaller 打包的程序常被误报

**解决**:
1. 使用代码签名证书
2. 提交样本到杀毒软件厂商
3. 提供源码和打包脚本

## 📝 版本管理

### 更新版本号

1. 修改 [stm32_flash.py:308](stm32_flash.py#L308)
   ```python
   self.setWindowTitle("STM32 固件烧录工具 v1.1")
   ```

2. 更新 [package.json:3](package.json#L3)（Electron 版本）
   ```json
   "version": "1.1.0",
   ```

3. 重新打包

### 发布检查清单

- [ ] 更新版本号
- [ ] 更新 CHANGELOG
- [ ] 测试所有功能
- [ ] 打包并测试
- [ ] 创建压缩包
- [ ] 编写发布说明
- [ ] 上传到发布平台
- [ ] 通知用户

## 🔐 代码签名（可选）

为了避免 Windows SmartScreen 警告：

```bash
# 需要购买代码签名证书
signtool sign /f certificate.pfx /p password /t http://timestamp.digicert.com dist/STM32_Flash_Tool/STM32_Flash_Tool.exe
```

## 📚 参考资料

- [PyInstaller 文档](https://pyinstaller.org/)
- [PySide6 文档](https://doc.qt.io/qtforpython/)
- [OpenOCD 文档](https://openocd.org/doc/)
