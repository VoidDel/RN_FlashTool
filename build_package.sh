#!/bin/bash
# STM32 固件烧录工具 - 打包脚本 (Linux/macOS)
# 使用方法: chmod +x build_package.sh && ./build_package.sh

set -e  # 遇到错误立即退出

echo "========================================"
echo "STM32 固件烧录工具 - 自动打包脚本"
echo "========================================"
echo ""

# 检查虚拟环境
if [ ! -d ".venv" ]; then
    echo "[错误] 未找到虚拟环境，请先创建虚拟环境"
    echo "运行: python3 -m venv .venv"
    exit 1
fi

# 激活虚拟环境
echo "[1/5] 激活虚拟环境..."
source .venv/bin/activate

# 检查依赖
echo "[2/5] 检查依赖..."
if ! pip show pyinstaller > /dev/null 2>&1; then
    echo "[安装] PyInstaller 未安装，正在安装..."
    pip install pyinstaller
fi

if ! pip show PySide6 > /dev/null 2>&1; then
    echo "[安装] PySide6 未安装，正在安装..."
    pip install PySide6
fi

# 清理旧的构建文件
echo "[3/5] 清理旧的构建文件..."
rm -rf build dist

# 执行打包
echo "[4/5] 开始打包..."
pyinstaller stm32_flash.spec --clean --noconfirm

# 检查打包结果
if [ ! -f "dist/STM32_Flash_Tool/STM32_Flash_Tool" ]; then
    echo ""
    echo "[错误] 打包失败！"
    exit 1
fi

# 复制说明文档
echo "[5/5] 复制说明文档..."
[ -f "README.md" ] && cp README.md dist/STM32_Flash_Tool/
[ -f "CLAUDE.md" ] && cp CLAUDE.md dist/STM32_Flash_Tool/

# 显示结果
echo ""
echo "========================================"
echo "打包完成！"
echo "========================================"
echo ""
echo "输出目录: dist/STM32_Flash_Tool/"
echo "主程序: STM32_Flash_Tool"
echo ""

# 显示大小
size=$(du -sh dist/STM32_Flash_Tool | cut -f1)
echo "总大小: $size"
echo ""

echo "提示: 可以使用以下命令压缩打包："
echo "  tar -czf STM32_Flash_Tool_v1.0.tar.gz -C dist STM32_Flash_Tool"
echo "  或"
echo "  zip -r STM32_Flash_Tool_v1.0.zip dist/STM32_Flash_Tool"
