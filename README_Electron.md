# STM32固件烧录工具 - Electron版本

基于Electron开发的现代化STM32固件烧录工具，提供美观的Web界面和强大的功能。

## 功能特点

- 🎨 现代化的Web界面
- 🔥 支持固件烧录（.elf和.bin格式）
- ✓ 支持固件验证
- 📥 支持固件读取
- 🔄 支持设备复位
- 🛠️ 支持多种编程器（ST-Link、J-Link、CMSIS-DAP）
- 📋 支持广泛的STM32芯片型号
- 📝 实时操作日志
- 🚀 多线程操作，界面响应流畅
- ⚠️ 智能错误分析和提示

## 系统要求

### Windows
- Windows 10或更高版本
- Node.js 16或更高版本

### macOS
- macOS 10.14或更高版本
- Node.js 16或更高版本

### Linux
- Ubuntu 18.04或更高版本 / 其他主流发行版
- Node.js 16或更高版本

### OpenOCD
- 已安装OpenOCD并添加到系统PATH，或
- 将OpenOCD可执行文件放在`openocd/bin/`目录下

## 安装步骤

### 1. 克隆或下载项目
```bash
git clone <repository-url>
cd STM32_FLASH
```

### 2. 安装依赖
```bash
npm install
```

### 3. 准备OpenOCD
- **Windows**: 下载OpenOCD Windows版本并解压到 `openocd` 目录
- **macOS**: `brew install openocd`
- **Linux**: `sudo apt-get install openocd`（Ubuntu/Debian）

### 4. 准备图标文件（可选）
在 `assets/` 目录下放置应用图标：
- `icon.png` (Linux/macOS)
- `icon.ico` (Windows)

### 5. 运行应用
```bash
# 开发模式
npm run dev

# 生产模式
npm start

# 构建应用（可选）
npm run build-win    # Windows
npm run build-linux  # Linux
npm run build-mac    # macOS
```

## 使用说明

### 1. 配置目标芯片
- 在"目标配置"区域选择或输入目标芯片型号
- 选择合适的编程器（ST-Link、J-Link或CMSIS-DAP）

### 2. 选择固件文件
- 点击"浏览"按钮选择.elf或.bin格式的固件文件
- 支持自动识别文件格式

### 3. 配置参数
- 设置烧录地址（默认为0x08000000）
- 对于读取操作，设置读取大小

### 4. 执行操作
- **烧录固件**: 将固件写入芯片
- **验证固件**: 比较固件文件与芯片内容
- **读取固件**: 从芯片读取固件到文件
- **复位设备**: 复位目标芯片

### 5. 查看日志
- 所有操作都会在"操作日志"区域显示详细信息
- 包含时间戳和操作状态

## 目录结构

```
STM32_FLASH/
├── main.js              # Electron主进程
├── preload.js            # 预加载脚本
├── index.html            # Web界面
├── package.json          # 项目配置
├── assets/               # 资源文件
│   └── icon.png         # 应用图标
├── openocd/              # OpenOCD可执行文件
│   └── bin/
└── temp/                # 临时配置文件
```

## 故障排除

### OpenOCD连接问题
如果遇到"cannot read IDR"错误：
1. 检查编程器连接是否正常
2. 确认目标芯片已上电
3. 检查SWD接线（SWDIO、SWCLK、GND）
4. 尝试降低连接速度

### 芯片识别错误
1. 检查目标芯片型号选择是否正确
2. 确认芯片没有被读保护
3. 尝试使用复位引脚

### 超时错误
1. 检查硬件连接
2. 确认芯片供电正常
3. 尝试降低连接速度

## 开发说明

### 修改界面
- 界面文件位于 `index.html`
- 样式使用内联CSS，便于修改
- 支持响应式设计

### 添加新功能
- 主进程逻辑在 `main.js`
- 与渲染进程通信通过 `preload.js`
- 支持OpenOCD命令扩展

### 打包发布
- 使用 `electron-builder` 进行跨平台打包
- 支持 Windows（NSIS）、Linux（AppImage）、macOS（DMG）

## 技术栈

- **框架**: Electron 28.0
- **UI**: HTML5 + CSS3 + JavaScript
- **后端**: Node.js
- **构建工具**: electron-builder

## 许可证

MIT License

## 贡献

欢迎提交Issue和Pull Request！

## 更新日志

### v1.0.0
- 初始版本发布
- 支持基本的烧录、验证、读取、复位功能
- 美观的Web界面
- 完整的错误处理和日志记录