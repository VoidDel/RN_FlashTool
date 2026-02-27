@echo off
REM STM32 固件烧录工具 - 打包脚本
REM 使用方法: 双击运行此脚本

echo ========================================
echo STM32 固件烧录工具 - 自动打包脚本
echo ========================================
echo.

REM 检查虚拟环境
if not exist ".venv\Scripts\activate.bat" (
    echo [错误] 未找到虚拟环境，请先创建虚拟环境
    echo 运行: python -m venv .venv
    pause
    exit /b 1
)

REM 激活虚拟环境
echo [1/5] 激活虚拟环境...
call .venv\Scripts\activate.bat

REM 检查依赖
echo [2/5] 检查依赖...
pip show pyinstaller >nul 2>&1
if errorlevel 1 (
    echo [安装] PyInstaller 未安装，正在安装...
    pip install pyinstaller
)

pip show PySide6 >nul 2>&1
if errorlevel 1 (
    echo [安装] PySide6 未安装，正在安装...
    pip install PySide6
)

REM 清理旧的构建文件
echo [3/5] 清理旧的构建文件...
if exist "build" rmdir /s /q build
if exist "dist" rmdir /s /q dist

REM 执行打包
echo [4/5] 开始打包...
pyinstaller stm32_flash.spec --clean --noconfirm

REM 检查打包结果
if not exist "dist\STM32_Flash_Tool\STM32_Flash_Tool.exe" (
    echo.
    echo [错误] 打包失败！
    pause
    exit /b 1
)

REM 复制说明文档
echo [5/5] 复制说明文档...
if exist "README.md" copy README.md dist\STM32_Flash_Tool\
if exist "CLAUDE.md" copy CLAUDE.md dist\STM32_Flash_Tool\

REM 显示结果
echo.
echo ========================================
echo 打包完成！
echo ========================================
echo.
echo 输出目录: dist\STM32_Flash_Tool\
echo 主程序: STM32_Flash_Tool.exe
echo.

REM 显示大小
echo 正在计算大小...
echo.

REM 询问是否打开目录
set /p open="是否打开输出目录? (Y/N): "
if /i "%open%"=="Y" explorer dist\STM32_Flash_Tool

echo.
echo 提示: 可以使用 7-Zip 压缩 dist\STM32_Flash_Tool 文件夹进行分发
pause
